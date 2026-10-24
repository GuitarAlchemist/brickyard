package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os/exec"
	"strings"
	"time"
)

// gatewayInvite is the phone gateway's own entry on this computer, for Delta's window (brickyard-remote's
// -local-port): POST /invite answers {"link", "expires"}, a link that lets guests watch the window from afar for an
// hour; 409 while a guest watches, since the window would show them the next one, and 404 when it lets no guest in.
const gatewayInvite = "http://127.0.0.1:8813/invite"

var (
	errWatching  = errors.New("a guest watches the window: invite from the paired phone")
	errGuestsOff = errors.New("the phone gateway lets no guest in: it needs -guest-host")
)

// invite asks the gateway at address for an invitation, as the window's Inviter button does, with the secret the
// gateway also sends this brain. The link lets someone watch: it goes to the window and the clipboard, never a log.
func (r *remote) invite(address string) (link string, until time.Time, err error) {
	req, err := http.NewRequest(http.MethodPost, address, nil)
	if err != nil {
		return "", time.Time{}, err
	}
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, r.secret)
	resp, err := (&http.Client{Timeout: 5 * time.Second}).Do(req)
	if err != nil {
		return "", time.Time{}, fmt.Errorf("no phone gateway answers on this computer: %w", err)
	}
	defer resp.Body.Close()
	switch resp.StatusCode {
	case http.StatusOK:
	case http.StatusConflict:
		return "", time.Time{}, errWatching
	case http.StatusNotFound:
		return "", time.Time{}, errGuestsOff
	default:
		return "", time.Time{}, fmt.Errorf("the phone gateway answered %s", resp.Status)
	}
	var body struct {
		Link    string    `json:"link"`
		Expires time.Time `json:"expires"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 4096)).Decode(&body); err != nil {
		return "", time.Time{}, fmt.Errorf("the phone gateway's invitation: %w", err)
	}
	if u, err := url.Parse(body.Link); err != nil || u.Scheme != "https" || u.Host == "" || body.Expires.IsZero() {
		return "", time.Time{}, errors.New("the phone gateway answered something other than an invitation")
	}
	return body.Link, body.Expires, nil
}

// invitationRefused names why the window shows no invitation, for its card: watching, off or gateway.
func invitationRefused(err error) string {
	switch {
	case errors.Is(err, errWatching):
		return "watching"
	case errors.Is(err, errGuestsOff):
		return "off"
	}
	return "gateway"
}

// copyText puts text on Windows' clipboard, for the user to paste the link into a message.
func copyText(text string) error {
	cmd := exec.Command("clip.exe")
	hideConsole(cmd)
	cmd.Stdin = strings.NewReader(text)
	return cmd.Run()
}
