package main

import (
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"math/big"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

// fakeTeam stands for a Cloudflare Zero Trust team: its keys at /cdn-cgi/access/certs, and the tokens it signs.
type fakeTeam struct {
	key     *rsa.PrivateKey
	kid     string
	fetches atomic.Int32
	certs   *httptest.Server
}

func newFakeTeam(t *testing.T) *fakeTeam {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	f := &fakeTeam{key: key, kid: "k1"}
	f.certs = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		f.fetches.Add(1)
		json.NewEncoder(w).Encode(map[string]any{"keys": []map[string]string{{
			"kid": f.kid, "kty": "RSA", "alg": "RS256",
			"n": base64.RawURLEncoding.EncodeToString(key.N.Bytes()),
			"e": base64.RawURLEncoding.EncodeToString(big.NewInt(int64(key.E)).Bytes()),
		}}})
	}))
	t.Cleanup(f.certs.Close)
	return f
}

func (f *fakeTeam) sign(t *testing.T, head, claims map[string]any) string {
	t.Helper()
	h, _ := json.Marshal(head)
	c, _ := json.Marshal(claims)
	signed := base64.RawURLEncoding.EncodeToString(h) + "." + base64.RawURLEncoding.EncodeToString(c)
	sum := sha256.Sum256([]byte(signed))
	sig, err := rsa.SignPKCS1v15(rand.Reader, f.key, crypto.SHA256, sum[:])
	if err != nil {
		t.Fatal(err)
	}
	return signed + "." + base64.RawURLEncoding.EncodeToString(sig)
}

func (f *fakeTeam) access(now time.Time) *access {
	a := newAccess("brickyard", "aud-tag", "Owner@Example.com")
	a.certs = f.certs.URL
	a.now = func() time.Time { return now }
	return a
}

func (f *fakeTeam) claims(now time.Time) map[string]any {
	return map[string]any{"aud": []string{"aud-tag"}, "iss": "https://brickyard.cloudflareaccess.com",
		"email": "owner@example.com", "exp": now.Add(time.Hour).Unix(), "nbf": now.Add(-time.Minute).Unix()}
}

func TestOnlyTheOwnersAccessTokenLetsIn(t *testing.T) {
	f := newFakeTeam(t)
	now := time.Unix(1_800_000_000, 0)
	a := f.access(now)
	rs256 := map[string]any{"alg": "RS256", "kid": "k1"}
	if err := a.verify(f.sign(t, rs256, f.claims(now))); err != nil {
		t.Fatalf("the owner's token: %v", err)
	}
	with := func(k string, v any) map[string]any {
		c := f.claims(now)
		c[k] = v
		return c
	}
	other, _ := rsa.GenerateKey(rand.Reader, 2048)
	forged := (&fakeTeam{key: other}).sign(t, rs256, f.claims(now))
	for name, token := range map[string]string{
		"no token":                   "",
		"another application":        f.sign(t, rs256, with("aud", []string{"other"})),
		"another team":               f.sign(t, rs256, with("iss", "https://evil.cloudflareaccess.com")),
		"another email":              f.sign(t, rs256, with("email", "guest@example.com")),
		"an expired token":           f.sign(t, rs256, with("exp", now.Add(-time.Second).Unix())),
		"a token not yet valid":      f.sign(t, rs256, with("nbf", now.Add(time.Hour).Unix())),
		"a signature by another key": forged,
		"alg none":                   f.sign(t, map[string]any{"alg": "none", "kid": "k1"}, f.claims(now)),
		"an unknown key":             f.sign(t, map[string]any{"alg": "RS256", "kid": "k2"}, f.claims(now)),
	} {
		if err := a.verify(token); err == nil {
			t.Errorf("%s let in", name)
		}
	}
	if n := f.fetches.Load(); n != 1 {
		t.Errorf("the team's keys were fetched %d times, want once: an unknown key asks again at most once a minute", n)
	}
	single := f.claims(now)
	single["aud"] = "aud-tag"
	if err := a.verify(f.sign(t, rs256, single)); err != nil {
		t.Errorf("a token whose aud is one string: %v", err)
	}
}

// ownerEntry opens g's entry for the owner away, behind the fake team's Access, at its own address.
func ownerEntry(t *testing.T, g *testGateway, f *fakeTeam) *httptest.Server {
	t.Helper()
	ts := httptest.NewServer(nil)
	g.s.ownerHost = ts.Listener.Addr().String()
	g.s.access = f.access(g.now)
	ts.Config.Handler = g.s.ownerHandler()
	t.Cleanup(ts.Close)
	return ts
}

// Away from home the owner gets the paired device's page and its connection, and nobody else gets anything.
func TestTheOwnersEntryOpensForAccessOnly(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	f := newFakeTeam(t)
	owner := ownerEntry(t, g, f)
	token := f.sign(t, map[string]any{"alg": "RS256", "kid": "k1"}, f.claims(g.now))
	call := func(method, path string, body []byte, token string) *http.Response {
		req, _ := http.NewRequest(method, owner.URL+path, strings.NewReader(string(body)))
		if token != "" {
			req.Header.Set("Cf-Access-Jwt-Assertion", token)
		}
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		return resp
	}
	for _, path := range []string{"/", "/page.js", "/ice"} {
		if resp := call(http.MethodGet, path, nil, ""); resp.StatusCode != http.StatusForbidden {
			t.Errorf("GET %s without Access: status %d, want 403", path, resp.StatusCode)
		}
		if resp := call(http.MethodGet, path, nil, token); resp.StatusCode != http.StatusOK {
			t.Errorf("GET %s with the owner's token: status %d, want 200", path, resp.StatusCode)
		}
	}
	if resp := call(http.MethodPost, "/pair", []byte(`{"code":"x"}`), token); resp.StatusCode != http.StatusNotFound && resp.StatusCode != http.StatusMethodNotAllowed {
		t.Errorf("POST /pair at the owner's entry: status %d, want none there", resp.StatusCode)
	}
	pc, body := offer(t)
	resp := call(http.MethodPost, "/offer", body, token)
	connect(t, pc, resp)
	_, device := g.pair(t, g.s.issueCode())
	_, second := offer(t)
	if resp := g.do(t, "/offer", second, device, g.ts.URL); resp.StatusCode != http.StatusConflict {
		t.Errorf("a device at home while the owner watches away: status %d, want 409", resp.StatusCode)
	}
}
