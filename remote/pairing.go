package main

import (
	"bufio"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base32"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

const (
	deviceCookie = "delta_device"
	codeLife     = 2 * time.Minute
	codeTries    = 5
	deviceLife   = 90 * 24 * time.Hour
)

// pairing holds the one pairing code a start of the gateway shows, and the devices paired so far. A code is single
// use, lives two minutes and is burnt after five wrong tries; a device keeps a random cookie whose SHA-256 is kept in
// the devices file, so the file never holds anything a device could present.
type pairing struct {
	path string // the devices file

	mu      sync.Mutex
	code    string
	expires time.Time
	misses  int
	devices map[string]bool // SHA-256 of each device cookie, hex
}

func loadPairing(home string) (*pairing, error) {
	p := &pairing{path: filepath.Join(home, "devices"), devices: map[string]bool{}}
	f, err := os.Open(p.path)
	if errors.Is(err, os.ErrNotExist) {
		return p, nil
	}
	if err != nil {
		return nil, err
	}
	defer f.Close()
	for sc := bufio.NewScanner(f); sc.Scan(); {
		if h := strings.TrimSpace(sc.Text()); len(h) == 64 {
			p.devices[h] = true
		}
	}
	return p, nil
}

// forgetDevices unpairs every device; the next start shows a new code.
func forgetDevices(home string) error {
	err := os.Remove(filepath.Join(home, "devices"))
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

// issue makes a new code, valid for two minutes from now, and returns it as XXXX-XXXX.
func (p *pairing) issue(now time.Time) string {
	b := make([]byte, 5)
	rand.Read(b)
	c := base32.StdEncoding.EncodeToString(b) // 8 characters, 40 bits
	p.mu.Lock()
	defer p.mu.Unlock()
	p.code, p.expires, p.misses = c, now.Add(codeLife), 0
	return c[:4] + "-" + c[4:]
}

// trade spends code for a new device cookie, or says why not.
func (p *pairing) trade(code string, now time.Time) (string, error) {
	code = strings.ToUpper(strings.ReplaceAll(strings.TrimSpace(code), "-", ""))
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.code == "" || now.After(p.expires) {
		return "", errors.New("no pairing code is open: restart delta-remote for a new one")
	}
	if subtle.ConstantTimeCompare([]byte(code), []byte(p.code)) != 1 {
		if p.misses++; p.misses >= codeTries {
			p.code = ""
		}
		return "", errors.New("wrong pairing code")
	}
	p.code = ""
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	cookie := hex.EncodeToString(b)
	h := digest(cookie)
	f, err := os.OpenFile(p.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return "", err
	}
	defer f.Close()
	if _, err := f.WriteString(h + "\n"); err != nil {
		return "", err
	}
	p.devices[h] = true
	return cookie, nil
}

func (p *pairing) known(cookie string) bool {
	if len(cookie) != 64 {
		return false
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.devices[digest(cookie)]
}

func digest(s string) string {
	h := sha256.Sum256([]byte(s))
	return hex.EncodeToString(h[:])
}
