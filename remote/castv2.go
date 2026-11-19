package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"
	"time"
)

// The Cast protocol a Chromecast speaks on TCP 8009, over TLS: each message is a CastMessage protocol buffer, after
// its length on four bytes, big-endian, and carries JSON on a namespace. The gateway has the Chromecast's default
// media receiver, which needs no application registered with Google, play the live stream it serves (tv.go).
const (
	nsConnection = "urn:x-cast:com.google.cast.tp.connection"
	nsHeartbeat  = "urn:x-cast:com.google.cast.tp.heartbeat"
	nsReceiver   = "urn:x-cast:com.google.cast.receiver"
	nsMedia      = "urn:x-cast:com.google.cast.media"

	defaultReceiver = "CC1AD845" // Google's default media receiver
	castSender      = "sender-0"
	castPlatform    = "receiver-0"
	maxCastMessage  = 64 << 10
)

// castMessage is the part of a CastMessage the gateway uses: a UTF-8 payload, never a binary one.
type castMessage struct {
	source, destination, namespace, payload string
}

// marshal encodes m as a CastMessage: protocol_version (1) CASTV2_1_0, source_id (2), destination_id (3),
// namespace (4), payload_type (5) STRING, payload_utf8 (6).
func (m castMessage) marshal() []byte {
	b := []byte{1<<3 | 0, 0}
	for _, f := range []struct {
		n int
		s string
	}{{2, m.source}, {3, m.destination}, {4, m.namespace}} {
		b = appendField(b, f.n, f.s)
	}
	b = append(b, 5<<3|0, 0)
	return appendField(b, 6, m.payload)
}

func appendField(b []byte, n int, s string) []byte {
	b = binary.AppendUvarint(b, uint64(n<<3|2))
	b = binary.AppendUvarint(b, uint64(len(s)))
	return append(b, s...)
}

// unmarshalCast decodes a CastMessage, skipping the fields the gateway does not use.
func unmarshalCast(b []byte) (castMessage, error) {
	var m castMessage
	for len(b) > 0 {
		key, n := binary.Uvarint(b)
		if n <= 0 {
			return m, errors.New("a CastMessage's key does not parse")
		}
		b = b[n:]
		switch key & 7 {
		case 0:
			_, n := binary.Uvarint(b)
			if n <= 0 {
				return m, errors.New("a CastMessage's number does not parse")
			}
			b = b[n:]
		case 2:
			size, n := binary.Uvarint(b)
			if n <= 0 || size > uint64(len(b)-n) {
				return m, errors.New("a CastMessage's field is longer than the message")
			}
			s := string(b[n : n+int(size)])
			b = b[n+int(size):]
			switch key >> 3 {
			case 2:
				m.source = s
			case 3:
				m.destination = s
			case 4:
				m.namespace = s
			case 6:
				m.payload = s
			}
		default:
			return m, fmt.Errorf("a CastMessage's field has wire type %d", key&7)
		}
	}
	return m, nil
}

// castConn is a connection to a Chromecast.
type castConn struct {
	conn net.Conn
	r    *bufio.Reader
	mu   sync.Mutex // one write at a time
}

// dialCast connects to the Chromecast at addr. Its certificate is its own, made by the device: there is nothing to
// check it against, and the stream it is told to play is no secret beyond this network.
func dialCast(addr string) (*castConn, error) {
	conn, err := tls.DialWithDialer(&net.Dialer{Timeout: 5 * time.Second}, "tcp", addr, &tls.Config{InsecureSkipVerify: true})
	if err != nil {
		return nil, err
	}
	return &castConn{conn: conn, r: bufio.NewReader(conn)}, nil
}

func (c *castConn) send(destination, namespace string, payload any) error {
	text, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	body := castMessage{castSender, destination, namespace, string(text)}.marshal()
	c.mu.Lock()
	defer c.mu.Unlock()
	c.conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
	_, err = c.conn.Write(binary.BigEndian.AppendUint32(nil, uint32(len(body))))
	if err == nil {
		_, err = c.conn.Write(body)
	}
	return err
}

func (c *castConn) read() (castMessage, error) {
	var size [4]byte
	if _, err := io.ReadFull(c.r, size[:]); err != nil {
		return castMessage{}, err
	}
	n := binary.BigEndian.Uint32(size[:])
	if n > maxCastMessage {
		return castMessage{}, fmt.Errorf("a CastMessage of %d bytes", n)
	}
	body := make([]byte, n)
	if _, err := io.ReadFull(c.r, body); err != nil {
		return castMessage{}, err
	}
	return unmarshalCast(body)
}

func (c *castConn) close() { c.conn.Close() }

// errStoppedOnTV is how castPlay ends when the default media receiver closes on the Chromecast: from its remote, from
// another device that casts, or from the Google Home application.
var errStoppedOnTV = errors.New("the Chromecast stopped showing Delta")

// castStatus is what the gateway reads of a Chromecast's messages.
type castStatus struct {
	Type   string `json:"type"`
	Reason string `json:"reason"`
	Status *struct {
		Applications []struct {
			AppID       string `json:"appId"`
			SessionID   string `json:"sessionId"`
			TransportID string `json:"transportId"`
		} `json:"applications"`
	} `json:"status"`
}

// castPlay has the Chromecast at addr play url, a live HLS stream, in its default media receiver. It holds the
// connection until ctx ends, which closes the receiver, or until the Chromecast stops it, and says why it ended.
// playing is called once the Chromecast has taken the stream.
func castPlay(ctx context.Context, addr, url string, playing func()) error {
	c, err := dialCast(addr)
	if err != nil {
		return err
	}
	defer c.close()
	messages := make(chan castMessage)
	failed := make(chan error, 1)
	go func() {
		for {
			m, err := c.read()
			if err != nil {
				failed <- err
				return
			}
			select {
			case messages <- m:
			case <-ctx.Done():
				return
			}
		}
	}()
	if err := c.send(castPlatform, nsConnection, map[string]string{"type": "CONNECT"}); err != nil {
		return err
	}
	if err := c.send(castPlatform, nsReceiver, map[string]any{"type": "LAUNCH", "appId": defaultReceiver, "requestId": 1}); err != nil {
		return err
	}
	ping := time.NewTicker(5 * time.Second)
	defer ping.Stop()
	session, loaded, state := "", false, ""
	for {
		select {
		case <-ctx.Done():
			if session != "" {
				c.send(castPlatform, nsReceiver, map[string]any{"type": "STOP", "sessionId": session, "requestId": 3})
			}
			return ctx.Err()
		case err := <-failed:
			return fmt.Errorf("the Chromecast's connection: %w", err)
		case <-ping.C:
			if err := c.send(castPlatform, nsHeartbeat, map[string]string{"type": "PING"}); err != nil {
				return err
			}
		case m := <-messages:
			var st castStatus
			json.Unmarshal([]byte(m.payload), &st)
			switch {
			case m.namespace == nsHeartbeat && st.Type == "PING":
				c.send(m.source, nsHeartbeat, map[string]string{"type": "PONG"})
			case m.namespace == nsReceiver && st.Type == "RECEIVER_STATUS" && st.Status != nil:
				found := false
				for _, app := range st.Status.Applications {
					if app.AppID != defaultReceiver || (session != "" && app.SessionID != session) {
						continue
					}
					found = true
					if loaded {
						continue
					}
					session, loaded = app.SessionID, true
					if err := c.send(app.TransportID, nsConnection, map[string]string{"type": "CONNECT"}); err != nil {
						return err
					}
					load := map[string]any{
						"type": "LOAD", "requestId": 2, "sessionId": session, "autoplay": true,
						"media": map[string]any{
							"contentId": url, "contentUrl": url, "contentType": "application/x-mpegURL", "streamType": "LIVE",
							"hlsSegmentFormat": "ts", "hlsVideoSegmentFormat": "mpeg2_ts",
							"metadata": map[string]any{"metadataType": 0, "title": "Delta"},
						},
					}
					if err := c.send(app.TransportID, nsMedia, load); err != nil {
						return err
					}
				}
				if loaded && !found {
					return errStoppedOnTV
				}
			case m.namespace == nsReceiver && (st.Type == "LAUNCH_ERROR" || st.Type == "INVALID_REQUEST"):
				return fmt.Errorf("the Chromecast refused to start its media receiver: %s %s", st.Type, st.Reason)
			case m.namespace == nsMedia && (st.Type == "LOAD_FAILED" || st.Type == "LOAD_CANCELLED" || st.Type == "INVALID_REQUEST"):
				return fmt.Errorf("the Chromecast did not take the stream: %s %s", st.Type, st.Reason)
			case m.namespace == nsMedia && st.Type == "MEDIA_STATUS":
				if now := mediaState(m.payload); now != "" && now != state {
					state = now
					logf("the television's player: %s", state)
				}
				if playing != nil && mediaPlays(m.payload) {
					playing()
					playing = nil
				}
				if why := mediaEnded(m.payload); why != "" {
					return fmt.Errorf("the Chromecast stopped the stream: %s", why)
				}
			}
		}
	}
}

// mediaStatus is a MEDIA_STATUS message's status list.
type mediaStatus struct {
	Status []struct {
		PlayerState string `json:"playerState"`
		IdleReason  string `json:"idleReason"`
	} `json:"status"`
}

// mediaState is the player's state a MEDIA_STATUS message gives, or "" when it gives none: a stall shows as BUFFERING.
func mediaState(payload string) string {
	var m mediaStatus
	json.Unmarshal([]byte(payload), &m)
	for _, s := range m.Status {
		if s.PlayerState != "" {
			return s.PlayerState
		}
	}
	return ""
}

func mediaPlays(payload string) bool {
	var m mediaStatus
	json.Unmarshal([]byte(payload), &m)
	for _, s := range m.Status {
		if s.PlayerState == "PLAYING" || s.PlayerState == "BUFFERING" {
			return true
		}
	}
	return false
}

// mediaEnded is why the player went idle on its own, or "": an error, or the stream finished.
func mediaEnded(payload string) string {
	var m mediaStatus
	json.Unmarshal([]byte(payload), &m)
	for _, s := range m.Status {
		if s.PlayerState == "IDLE" && (s.IdleReason == "ERROR" || s.IdleReason == "FINISHED" || s.IdleReason == "CANCELLED") {
			return s.IdleReason
		}
	}
	return ""
}
