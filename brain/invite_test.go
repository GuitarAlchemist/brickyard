package main

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"testing"
	"time"
)

// The window's Inviter button asks the gateway's own entry with the brain's secret, as the gateway asks the brain.
func TestTheWindowAsksTheGatewayForAnInvitation(t *testing.T) {
	expires := time.Date(2026, 10, 24, 15, 10, 0, 0, time.UTC)
	status := http.StatusOK
	gateway := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		if req.Method != http.MethodPost || req.Header.Get(hookHeader) != "1" || req.Header.Get(remoteHeader) != "s3cret" || req.Header.Get("Origin") != "" {
			http.Error(w, "only Delta's brain asks here", http.StatusForbidden)
			return
		}
		if status != http.StatusOK {
			http.Error(w, "no", status)
			return
		}
		w.Write([]byte(`{"link":"https://delta.example/#invite=0123456789abcdef0123456789abcdef","expires":"2026-10-24T15:10:00Z"}`))
	}))
	defer gateway.Close()
	r := &remote{secret: "s3cret"}

	link, until, err := r.invite(gateway.URL + "/invite")
	if err != nil || link != "https://delta.example/#invite=0123456789abcdef0123456789abcdef" || !until.Equal(expires) {
		t.Fatalf("invite = %q, %v, %v", link, until, err)
	}
	if rows, err := qrEncode(link); err != nil || len(rows) == 0 {
		t.Errorf("the link does not fit a QR code: %v", err)
	}
	for _, c := range []struct {
		status int
		want   string
	}{{http.StatusConflict, "watching"}, {http.StatusNotFound, "off"}, {http.StatusForbidden, "gateway"}} {
		status = c.status
		if _, _, err := r.invite(gateway.URL + "/invite"); err == nil || invitationRefused(err) != c.want {
			t.Errorf("%d: %v, refused %q, want %q", c.status, err, invitationRefused(err), c.want)
		}
	}
	gateway.Close()
	if _, _, err := r.invite(gateway.URL + "/invite"); err == nil || invitationRefused(err) != "gateway" {
		t.Errorf("no gateway: %v", err)
	}
	if invitationRefused(errors.New("other")) != "gateway" {
		t.Error("an unknown refusal is the gateway's")
	}
}

// A click on the guest badge switches whether guests hear the room, with the brain's secret.
func TestTheBadgeSwitchesWhetherGuestsHearTheRoom(t *testing.T) {
	var asked []string
	gateway := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		if req.Header.Get(hookHeader) != "1" || req.Header.Get(remoteHeader) != "s3cret" {
			http.Error(w, "no", http.StatusForbidden)
			return
		}
		body, _ := io.ReadAll(req.Body)
		asked = append(asked, string(body))
		w.WriteHeader(http.StatusNoContent)
	}))
	defer gateway.Close()
	r := &remote{secret: "s3cret"}
	if err := r.guestMic(gateway.URL, true); err != nil {
		t.Fatal(err)
	}
	if err := r.guestMic(gateway.URL, false); err != nil || !slices.Equal(asked, []string{`{"on":true}`, `{"on":false}`}) {
		t.Fatalf("%v, asked %v", err, asked)
	}
	r.secret = "other"
	if err := r.guestMic(gateway.URL, true); err == nil {
		t.Error("the gateway takes a wrong secret")
	}
}
