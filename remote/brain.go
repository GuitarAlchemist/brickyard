package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

// maxHear is the longest WAV the brain's /hear takes: 30 s of 16 kHz mono 16-bit, plus room for the header.
const maxHear = 30*16000*2 + 1024

// brain talks to Delta's brain on this computer (brain/remote.go): /hear takes what the device said, /said streams
// what Delta says, /link shows the pairing link as a QR in Delta's window. The brain rewrites its secret at each
// start, so it is read from the file on each request; without it, Delta is not listening.
type brain struct {
	base, secretPath string
	client           *http.Client // short requests: /hear and /link
	stream           *http.Client // /said, open as long as the device listens
}

func newBrain(base, secretPath string) *brain {
	return &brain{
		base:       strings.TrimRight(base, "/"),
		secretPath: secretPath,
		client:     &http.Client{Timeout: 60 * time.Second},
		stream:     &http.Client{},
	}
}

var errNoBrain = errors.New("Delta's brain is not running, or has no remote secret yet")

func (b *brain) request(ctx context.Context, method, path string, body io.Reader) (*http.Request, error) {
	s, err := os.ReadFile(b.secretPath)
	if err != nil || len(strings.TrimSpace(string(s))) != 64 {
		return nil, errNoBrain
	}
	req, err := http.NewRequestWithContext(ctx, method, b.base+path, body)
	if err != nil {
		return nil, err
	}
	req.Header.Set("X-Delta-Hook", "1")
	req.Header.Set("X-Delta-Remote", strings.TrimSpace(string(s)))
	return req, nil
}

func (b *brain) hear(ctx context.Context, wav []byte) (*http.Response, error) {
	req, err := b.request(ctx, http.MethodPost, "/hear", bytes.NewReader(wav))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "audio/wav")
	return b.client.Do(req)
}

// said streams what Delta says. plays: the page's sound runs, and it plays the answers to the device's own sentences,
// so the brain may leave those out at the computer, where they would echo. A page whose sound a touch has not
// unlocked yet would play none of them, so the computer must.
func (b *brain) said(ctx context.Context, plays bool) (*http.Response, error) {
	req, err := b.request(ctx, http.MethodGet, "/said", nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "text/event-stream")
	if plays {
		req.Header.Set("X-Delta-Plays", "own")
	}
	return b.stream.Do(req)
}

// link shows url as a QR in Delta's window; "" takes it away.
func (b *brain) link(url string) error {
	body, _ := json.Marshal(map[string]string{"url": url})
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	req, err := b.request(ctx, http.MethodPost, "/link", bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := b.client.Do(req)
	if err != nil {
		return err
	}
	resp.Body.Close()
	if resp.StatusCode/100 != 2 {
		return errors.New("the brain answered " + resp.Status + " to /link")
	}
	return nil
}
