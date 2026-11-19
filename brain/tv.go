package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// gatewayTV is the phone gateway's entry for casting Delta's window to a Chromecast (brickyard-remote's tv.go, on
// -local-port): POST find, start {"name"?}, stop and state. Its routes answer 404 "the television is off" when the gateway
// runs with -tv-port 0. The window's Caster button asks through this brain, which holds the gateway's secret (the user,
// 2026-11-18, « 1 - oui » to the button).
const gatewayTV = "http://127.0.0.1:8813/tv/"

// tvLine is what the window is told of the television: the Chromecast cast to and whether it plays, why the last cast
// ended, the Chromecasts to choose from when several answer, or why nothing was cast: off, none, many, old (a gateway
// without the /tv routes), gateway (none answers on this computer) or error.
type tvLine struct {
	On          string   `json:"on,omitempty"`
	Playing     bool     `json:"playing,omitempty"`
	Ended       string   `json:"ended,omitempty"`
	Chromecasts []string `json:"chromecasts,omitempty"`
	Refused     string   `json:"refused,omitempty"`
}

// tvPost asks the gateway's route at base+route, with the secret, and returns its status and up to 4 KB of its body.
func (r *remote) tvPost(base, route string, body string) (int, []byte, error) {
	req, err := http.NewRequest(http.MethodPost, base+route, strings.NewReader(body))
	if err != nil {
		return 0, nil, err
	}
	if body != "" {
		req.Header.Set("Content-Type", "application/json")
	}
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, r.secret)
	resp, err := (&http.Client{Timeout: 15 * time.Second}).Do(req) // finding the Chromecasts takes two seconds
	if err != nil {
		return 0, nil, err
	}
	defer resp.Body.Close()
	answer, err := io.ReadAll(io.LimitReader(resp.Body, 4096))
	return resp.StatusCode, answer, err
}

// tvOff is whether an answer says the gateway casts to no television.
func tvOff(status int, body []byte) bool {
	return status == http.StatusNotFound && strings.Contains(string(body), "television is off")
}

// tvNone is whether an answer says no Chromecast answers (the gateway's errNoChromecast). Any other 404 comes from a
// gateway older than its /tv routes, whose mux answers « 404 page not found » (Delta - remote, 2026-11-18).
func tvNone(status int, body []byte) bool {
	return status == http.StatusNotFound && strings.Contains(string(body), "no Chromecast")
}

// shortLine keeps the first line of a gateway's message, at most 200 characters, for the window's card.
func shortLine(body []byte) string {
	line, _, _ := strings.Cut(strings.TrimSpace(string(body)), "\n")
	if len(line) > 200 {
		line = line[:200]
	}
	return line
}

// tv does what the window's Caster button asked: "start" casts to the Chromecast named, or to the only one, or lists
// them when several answer; "stop" ends the cast; "state" says how it stands.
func (r *remote) tv(base, action, name string) tvLine {
	refused := func(why string, status int, body []byte) tvLine {
		switch {
		case tvOff(status, body):
			return tvLine{Refused: "off"}
		case tvNone(status, body):
			return tvLine{Refused: "none"}
		case status == http.StatusNotFound:
			return tvLine{Refused: "old"}
		}
		return tvLine{Refused: why, Ended: shortLine(body)}
	}
	switch action {
	case "start":
		if name == "" {
			status, body, err := r.tvPost(base, "find", "")
			if err != nil {
				return tvLine{Refused: "gateway"}
			}
			if status != http.StatusOK {
				return refused("error", status, body)
			}
			var found struct {
				Chromecasts []struct {
					Name string `json:"name"`
				} `json:"chromecasts"`
			}
			if err := json.Unmarshal(body, &found); err != nil {
				return tvLine{Refused: "error", Ended: "the gateway's list of Chromecasts did not read"}
			}
			names := make([]string, 0, len(found.Chromecasts))
			for _, c := range found.Chromecasts {
				if c.Name != "" && len(names) < 12 {
					names = append(names, c.Name)
				}
			}
			switch len(names) {
			case 0:
				return tvLine{Refused: "none"}
			case 1:
				name = names[0]
			default:
				return tvLine{Refused: "many", Chromecasts: names}
			}
		}
		ask, _ := json.Marshal(map[string]string{"name": name})
		status, body, err := r.tvPost(base, "start", string(ask))
		switch {
		case err != nil:
			return tvLine{Refused: "gateway"}
		case status != http.StatusOK:
			return refused("error", status, body)
		}
		var on tvLine
		if err := json.Unmarshal(body, &on); err != nil || on.On == "" {
			return tvLine{Refused: "error", Ended: "the gateway's answer did not read"}
		}
		return tvLine{On: on.On}
	case "stop", "state":
		if action == "stop" {
			status, body, err := r.tvPost(base, "stop", "")
			if err != nil {
				return tvLine{Refused: "gateway"}
			}
			if status != http.StatusNoContent && status != http.StatusOK {
				return refused("error", status, body)
			}
		}
		status, body, err := r.tvPost(base, "state", "")
		if err != nil {
			return tvLine{Refused: "gateway"}
		}
		if status != http.StatusOK {
			return refused("error", status, body)
		}
		var now tvLine
		if err := json.Unmarshal(body, &now); err != nil {
			return tvLine{Refused: "error", Ended: "the gateway's answer did not read"}
		}
		return tvLine{On: now.On, Playing: now.Playing, Ended: now.Ended}
	}
	return tvLine{Refused: "error", Ended: fmt.Sprintf("the window asked for %q", action)}
}
