package main

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"
)

// fakeTV answers as brickyard-remote's tv.go does (ae38aca): POST find, start {"name"}, stop, state, with the brain's
// secret and no Origin; off, every route answers 404 "the television is off".
type fakeTV struct {
	names   []string
	on      string
	playing bool
	off     bool
	asked   []string
}

func (f *fakeTV) ServeHTTP(w http.ResponseWriter, req *http.Request) {
	if req.Method != http.MethodPost || req.Header.Get(hookHeader) != "1" || req.Header.Get(remoteHeader) != "s3cret" || req.Header.Get("Origin") != "" {
		http.Error(w, "only Delta's brain asks here", http.StatusForbidden)
		return
	}
	route := strings.TrimPrefix(req.URL.Path, "/tv/")
	body, _ := io.ReadAll(req.Body)
	f.asked = append(f.asked, route+string(body))
	if f.off {
		http.Error(w, "the television is off", http.StatusNotFound)
		return
	}
	switch route {
	case "find":
		list := []map[string]string{}
		for _, n := range f.names {
			list = append(list, map[string]string{"name": n})
		}
		json.NewEncoder(w).Encode(map[string]any{"chromecasts": list})
	case "start":
		var ask struct{ Name string }
		json.Unmarshal(body, &ask)
		if !slices.Contains(f.names, ask.Name) {
			if len(f.names) == 0 {
				http.Error(w, "no Chromecast answers on this network", http.StatusNotFound)
				return
			}
			http.Error(w, "no Chromecast named that answers", http.StatusConflict)
			return
		}
		f.on = ask.Name
		json.NewEncoder(w).Encode(map[string]any{"on": f.on})
	case "stop":
		f.on, f.playing = "", false
		w.WriteHeader(http.StatusNoContent)
	case "state":
		if f.on == "" {
			json.NewEncoder(w).Encode(map[string]any{"ended": "stopped on the television"})
			return
		}
		json.NewEncoder(w).Encode(map[string]any{"on": f.on, "playing": f.playing})
	default:
		http.NotFound(w, req)
	}
}

// The Caster button casts to the only Chromecast, lists them when several answer, stops, and says when the gateway
// casts to no television or does not answer.
func TestTheCasterButtonAsksTheGateway(t *testing.T) {
	f := &fakeTV{names: []string{"Salon"}}
	gateway := httptest.NewServer(f)
	defer gateway.Close()
	r := &remote{secret: "s3cret"}
	base := gateway.URL + "/tv/"

	if got := r.tv(base, "start", ""); got.On != "Salon" || got.Refused != "" {
		t.Fatalf("start with one Chromecast = %+v", got)
	}
	if !slices.Equal(f.asked, []string{"find", `start{"name":"Salon"}`}) {
		t.Errorf("asked %q", f.asked)
	}
	f.playing = true
	if got := r.tv(base, "state", ""); got.On != "Salon" || !got.Playing {
		t.Errorf("state = %+v", got)
	}
	if got := r.tv(base, "stop", ""); got.On != "" || got.Ended == "" || got.Refused != "" {
		t.Errorf("stop = %+v", got)
	}

	f.names = []string{"Salon", "Chambre"}
	if got := r.tv(base, "start", ""); got.Refused != "many" || !slices.Equal(got.Chromecasts, []string{"Salon", "Chambre"}) || got.On != "" {
		t.Errorf("start with two = %+v", got)
	}
	if got := r.tv(base, "start", "Chambre"); got.On != "Chambre" {
		t.Errorf("start Chambre = %+v", got)
	}

	f.names = nil
	if got := r.tv(base, "start", ""); got.Refused != "none" {
		t.Errorf("no Chromecast = %+v", got)
	}
	f.off = true
	for _, action := range []string{"start", "stop", "state"} {
		if got := r.tv(base, action, ""); got.Refused != "off" {
			t.Errorf("%s with the television off = %+v", action, got)
		}
	}
	if got := r.tv(base, "dance", ""); got.Refused != "error" {
		t.Errorf("an unknown action = %+v", got)
	}

	r.secret = "other"
	f.off = false
	if got := r.tv(base, "state", ""); got.Refused != "error" {
		t.Errorf("a wrong secret = %+v", got)
	}
	gateway.Close()
	r.secret = "s3cret"
	if got := r.tv(base, "start", ""); got.Refused != "gateway" {
		t.Errorf("no gateway = %+v", got)
	}
}
