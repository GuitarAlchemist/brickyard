package main

import (
	"encoding/binary"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestEndpointCutsAnUtterance(t *testing.T) {
	var cut endpoint
	frame := func(level float32) []float32 {
		f := make([]float32, frameSamples)
		for i := range f {
			f[i] = level * float32(1-2*(i%2))
		}
		return f
	}
	var events []speechEvent
	var got []float32
	feed := func(n int, level float32) {
		for range n {
			event, speech := cut.feed(frame(level))
			if event != noSpeechEvent {
				events = append(events, event)
			}
			if speech != nil {
				got = speech
			}
		}
	}
	feed(40, 0.002) // the room, measured, then quiet
	feed(25, 0.2)   // half a second of speech
	feed(40, 0.002) // quiet: the cut comes 700 ms in
	if len(events) != 2 || events[0] != speechStarted || events[1] != speechEnded {
		t.Fatalf("events %v", events)
	}
	// The 300 ms before the start (which comes on the third loud frame), the rest of the speech, 700 ms of quiet.
	if frames := len(got) / frameSamples; frames != 15+(25-3)+35 {
		t.Fatalf("%d frames", frames)
	}

	events, got = nil, nil
	feed(5, 0.2) // a knock
	feed(40, 0.002)
	if len(events) != 2 || events[1] != speechEnded || got != nil {
		t.Fatalf("a knock: %v, %d samples", events, len(got))
	}
}

func TestEarsHear(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		file, _, err := r.FormFile("file")
		if err != nil || r.URL.Path != "/inference" || r.FormValue("response_format") != "json" {
			t.Errorf("%s %v %q", r.URL.Path, err, r.FormValue("response_format"))
			return
		}
		data, _ := io.ReadAll(file)
		if string(data[:4]) != "RIFF" || binary.LittleEndian.Uint32(data[24:]) != earsRate {
			t.Errorf("not a 16 kHz WAV")
		}
		io.WriteString(w, `{"text":" Bonjour   Delta. \n"}`)
	}))
	defer server.Close()
	text, err := (&ears{url: server.URL}).hear(make([]float32, 1600))
	if err != nil || text != "Bonjour Delta." {
		t.Fatalf("%q %v", text, err)
	}
}

// fakeWhisper is whisper-server as the test binary plays it, copied under a name that says how it behaves: "broken"
// stops at once, as a GPU build without its libraries; "dies" hears its first request, the warm-up, then stops at the
// next; any other answers every request with its own name.
func fakeWhisper() {
	name := strings.TrimSuffix(filepath.Base(os.Args[0]), ".exe")
	if name == "broken" {
		os.Exit(1)
	}
	port := ""
	for i, a := range os.Args {
		if a == "--port" {
			port = os.Args[i+1]
		}
	}
	heard := 0
	http.HandleFunc("/inference", func(w http.ResponseWriter, r *http.Request) {
		if heard++; name == "dies" && heard > 1 {
			os.Exit(2)
		}
		if name == "hangs" && heard > 1 { // as a graphics driver that stopped answering
			time.Sleep(time.Hour)
		}
		fmt.Fprintf(w, `{"text":%q}`, name)
	})
	http.ListenAndServe("127.0.0.1:"+port, nil)
	os.Exit(3)
}

// A GPU build of whisper.cpp that cannot start, dies or hangs leaves Delta hearing with the next server, the CPU build;
// so does a chain of servers that cannot start, and a sentence heard while another takes over.
func TestTheEarsFallBackOnTheNextServer(t *testing.T) {
	t.Setenv("BRAIN_FAKE_WHISPER", "1")
	self, err := os.ReadFile(os.Args[0])
	if err != nil {
		t.Fatal(err)
	}
	server := func(name string) string {
		path := filepath.Join(t.TempDir(), name+".exe")
		if err := os.WriteFile(path, self, 0o755); err != nil {
			t.Fatal(err)
		}
		return path
	}
	broken, dies, hangs, cpu := server("broken"), server("dies"), server("hangs"), server("cpu")
	start := func(servers ...string) *ears {
		e, err := startEars(servers, "model.bin", "fr", freePort(), 1, "", 0)
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(e.close)
		return e
	}

	e := start(broken, cpu)
	if text, err := e.hear(make([]float32, 1600)); text != "cpu" || err != nil || e.server != cpu || len(e.failed) != 1 {
		t.Fatalf("%q %v, %s after %v", text, err, e.server, e.failed)
	}

	e = start(dies, cpu)
	var fell []string
	e.fell = func(server, why string) { fell = append(fell, server, why) }
	if text, err := e.hear(make([]float32, 1600)); text != "cpu" || err != nil {
		t.Fatalf("after the first server died: %q %v", text, err)
	}
	if len(fell) != 2 || fell[0] != cpu || !strings.Contains(fell[1], "dies.exe died") {
		t.Fatalf("%q", fell)
	}

	e = start(dies, broken, cpu)
	fell = nil
	e.fell = func(server, why string) { fell = append(fell, server, why) }
	if text, err := e.hear(make([]float32, 1600)); text != "cpu" || err != nil {
		t.Fatalf("after a chain of failures: %q %v", text, err)
	}
	if len(fell) != 2 || !strings.Contains(fell[1], "dies.exe died: Post") || !strings.Contains(fell[1], "broken.exe stopped") {
		t.Fatalf("the message names each server with its own failure: %q", fell)
	}

	e = start(hangs, cpu)
	e.patience = 300 * time.Millisecond
	fell = nil
	e.fell = func(server, why string) { fell = append(fell, server, why) }
	if text, err := e.hear(make([]float32, 1600)); text != "cpu" || err != nil || len(fell) != 2 || !strings.Contains(fell[1], "hangs.exe did not answer") {
		t.Fatalf("after the first server hung: %q %v %q", text, err, fell)
	}

	e = start(dies, cpu)
	var wg sync.WaitGroup
	texts, errs := make([]string, 2), make([]error, 2)
	for i := range 2 { // the microphone and another device at once
		wg.Add(1)
		go func() {
			defer wg.Done()
			texts[i], errs[i] = e.hear(make([]float32, 1600))
		}()
	}
	wg.Wait()
	if texts[0] != "cpu" || texts[1] != "cpu" || errs[0] != nil || errs[1] != nil {
		t.Fatalf("two sentences while the next server takes over: %q %v", texts, errs)
	}

	if _, err := startEars([]string{broken}, "model.bin", "fr", freePort(), 1, "", 0); err == nil || !strings.Contains(err.Error(), "broken.exe") {
		t.Fatalf("no server can hear: %v", err)
	}
}

func TestCleanHeardDropsWhatWhisperInvents(t *testing.T) {
	for in, want := range map[string]string{
		" Sous-titres réalisés par la communauté d'Amara.org": "",
		" Thanks for watching!":                               "",
		"Thank you for watching.":                             "",
		"[Musique]":                                           "",
		"[Music]":                                             "",
		" Hello Delta, can you hear me? ":                     "Hello Delta, can you hear me?",
		"(rires)":                                             "",
		" ... ":                                               "",
		" Bonjour  Delta, tu m'entends ? ":                    "Bonjour Delta, tu m'entends ?",
	} {
		if got := cleanHeard(in); got != want {
			t.Errorf("%q: got %q", in, got)
		}
	}
}

func TestResampleAndWavRoundTrip(t *testing.T) {
	in := make([]float32, 22050)
	for i := range in {
		in[i] = float32(i) / 22050
	}
	out := resample(in, 22050, earsRate)
	if len(out) != earsRate || math.Abs(float64(out[8000])-0.5) > 1e-3 {
		t.Fatalf("%d samples, middle %v", len(out), out[8000])
	}
	path := filepath.Join(t.TempDir(), "r.wav")
	if err := os.WriteFile(path, encodeWav(out, earsRate), 0o644); err != nil {
		t.Fatal(err)
	}
	w, err := readWav(path)
	if err != nil || w.rate != earsRate || len(w.samples) != len(out) || math.Abs(float64(w.samples[8000]-out[8000])) > 1e-4 {
		t.Fatalf("%v: rate %d, %d samples", err, w.rate, len(w.samples))
	}
}

// Whisper sometimes writes one sentence over and over, as over a button held on silence: three copies in a row or more
// are one. Two are kept, since people repeat themselves.
func TestASentenceWhisperRepeatsIsHeardOnce(t *testing.T) {
	for _, c := range []struct {
		in, want string
		removed  int
	}{
		{"Voilà, quel est le dépôt ICT? " + strings.Repeat("Parfait, il y a un peu plus de temps. ", 8),
			"Voilà, quel est le dépôt ICT? Parfait, il y a un peu plus de temps.", 7},
		{"Et puis, on voit un compteur. Et puis on voit un compteur ! Et puis, on voit un compteur. Fin.",
			"Et puis, on voit un compteur. Fin.", 2},
		{"Je veux un chat. Le chat est pourri. Je veux un chat. Le chat est pourri.",
			"Je veux un chat. Le chat est pourri. Je veux un chat. Le chat est pourri.", 0},
		{"Delta. Delta.", "Delta. Delta.", 0},
		{"Oui oui oui", "Oui oui oui", 0},
	} {
		if got, removed := collapseRepeats(c.in); got != c.want || removed != c.removed {
			t.Errorf("%q: %q, %d removed", c.in, got, removed)
		}
	}
}

// Bilingual, Whisper detects the language each sentence was spoken in; one it takes for a third language, as it can
// a short sentence, is heard again in the likelier of Delta's two. With one language, Whisper is told it.
func TestBilingualEarsTellTheLanguage(t *testing.T) {
	var mu sync.Mutex
	var asked []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		lang := r.FormValue("language")
		mu.Lock()
		asked = append(asked, lang+"/"+r.FormValue("response_format"))
		mu.Unlock()
		if lang == "auto" {
			io.WriteString(w, `{"text":"Hallo Delta","language_probabilities":{"de":0.6,"en":0.3,"fr":0.1}}`)
			return
		}
		io.WriteString(w, `{"text":"Hello Delta"}`)
	}))
	defer server.Close()
	e := &ears{url: server.URL, languages: func() []string { return []string{"fr", "en"} }}
	text, lang, err := e.hearIn(make([]float32, 1600))
	if err != nil || text != "Hello Delta" || lang != "en" || strings.Join(asked, " ") != "auto/verbose_json en/json" {
		t.Fatalf("%q %q %v %v", text, lang, err, asked)
	}
	asked = nil
	e.languages = func() []string { return []string{"fr"} }
	if _, lang, err := e.hearIn(make([]float32, 1600)); err != nil || lang != "fr" || strings.Join(asked, " ") != "fr/json" {
		t.Fatalf("%q %v %v", lang, err, asked)
	}
}
