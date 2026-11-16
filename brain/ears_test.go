package main

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"io"
	"math"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
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
// next; "deaf" stops at its first request; any other answers every request with its own name. As whisper.cpp does, it
// serves its routes under --request-path, and nothing outside it, and on an argument it does not take it prints the
// usage and exits 0 (examples/server/server.cpp): "old" plays a build that does not take --request-path.
func fakeWhisper() {
	name := strings.TrimSuffix(filepath.Base(os.Args[0]), ".exe")
	if name == "broken" {
		os.Exit(1)
	}
	takes := map[string]bool{"-m": true, "-l": true, "-t": true, "--host": true, "--port": true, "--prompt": true, "-ac": true,
		"--request-path": name != "old", "-nf": false} // whether each takes a value
	port, prefix := "", ""
	for i := 1; i < len(os.Args); i++ {
		a := os.Args[i]
		value, known := takes[a]
		if !known || (a == "--request-path" && name == "old") {
			fmt.Fprintf(os.Stderr, "error: unknown argument: %s\n", a)
			os.Exit(0)
		}
		if !value {
			continue
		}
		i++
		switch a {
		case "--port":
			port = os.Args[i]
		case "--request-path":
			prefix = os.Args[i]
		}
	}
	heard := 0
	http.HandleFunc(prefix+"/inference", func(w http.ResponseWriter, r *http.Request) {
		if name == "deaf" { // stops at its first request, the warm-up
			os.Exit(2)
		}
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
	server := func(name string) string { return fakeWhisperExe(t, name) }
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

// fakeWhisperExe copies the test binary as a fake whisper-server named name (fakeWhisper).
func fakeWhisperExe(t *testing.T, name string) string {
	t.Helper()
	self, err := os.ReadFile(os.Args[0])
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), name+".exe")
	if err := os.WriteFile(path, self, 0o755); err != nil {
		t.Fatal(err)
	}
	return path
}

// whisper.cpp answers every request with Access-Control-Allow-Origin: * and checks neither Origin nor Host, so a web
// page can reach its loopback port. Its routes sit under a path drawn at each start, which the brain alone knows: a
// request to /inference or /load itself finds nothing, and two starts draw two paths.
func TestTheEarsRoutesAreUnguessable(t *testing.T) {
	t.Setenv("BRAIN_FAKE_WHISPER", "1")
	cpu := fakeWhisperExe(t, "cpu")
	var paths []string
	for range 2 {
		port := freePort()
		e, err := startEars([]string{cpu}, "model.bin", "fr", port, 1, "", 0)
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(e.close)
		if text, err := e.hear(make([]float32, 1600)); text != "cpu" || err != nil {
			t.Fatalf("the brain hears under its path: %q %v", text, err)
		}
		root := "http://127.0.0.1:" + strconv.Itoa(port)
		path := strings.TrimPrefix(e.url, root)
		if len(path) != 33 || path[0] != '/' || strings.Trim(path[1:], "0123456789abcdef") != "" {
			t.Fatalf("the ears' path %q is not / and 32 hex digits", path)
		}
		for _, guess := range []string{"/inference", "/load", "/"} {
			resp, err := http.Post(root+guess, "multipart/form-data; boundary=x", strings.NewReader("--x--\r\n"))
			if err != nil {
				t.Fatal(err)
			}
			resp.Body.Close()
			if resp.StatusCode != http.StatusNotFound {
				t.Errorf("a page that posts to %s gets %d, want 404", guess, resp.StatusCode)
			}
		}
		paths = append(paths, path)
	}
	if paths[0] == paths[1] {
		t.Fatalf("two starts drew the same path %q", paths[0])
	}
}

// whisper-server exits 0 at once on an argument it does not take, as a build that predates --request-path does on it:
// the error names the server and the argument it may lack, rather than a bare exit status.
func TestAnEarsServerWithoutRequestPathIsNamed(t *testing.T) {
	t.Setenv("BRAIN_FAKE_WHISPER", "1")
	old := fakeWhisperExe(t, "old")
	_, err := startEars([]string{old}, "model.bin", "fr", freePort(), 1, "", 0)
	if err == nil || !strings.Contains(err.Error(), "old.exe") || !strings.Contains(err.Error(), "--request-path") {
		t.Fatalf("a server that does not take --request-path: %v", err)
	}
	broken := fakeWhisperExe(t, "broken")
	if _, err := startEars([]string{broken}, "model.bin", "fr", freePort(), 1, "", 0); err == nil || strings.Contains(err.Error(), "--request-path") {
		t.Fatalf("a server that fails with its own status is not blamed on --request-path: %v", err)
	}
}

// The contract with the real whisper-server, on a throwaway instance of the build BRAIN_REAL_WHISPER names with the
// model BRAIN_REAL_WHISPER_MODEL, started with the brain's own arguments: its routes answer under the path only, and a
// /load posted at the root, with a file that is no model, neither reaches it nor stops the server. It sends no audio,
// never writes the path, and is skipped without both variables (CI has no real build).
func TestTheRealWhisperServerKeepsItsRoutesUnderThePath(t *testing.T) {
	server, model := os.Getenv("BRAIN_REAL_WHISPER"), os.Getenv("BRAIN_REAL_WHISPER_MODEL")
	if server == "" || model == "" {
		t.Skip("BRAIN_REAL_WHISPER and BRAIN_REAL_WHISPER_MODEL name a real whisper-server and its model")
	}
	port, path := freePort(), earsPath()
	root := "http://127.0.0.1:" + strconv.Itoa(port)
	cmd := exec.Command(server, earsArgs(model, "en", port, 2, "", 0, path)...)
	hideConsole(cmd)
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	dead := make(chan struct{})
	go func() { cmd.Wait(); close(dead) }()
	t.Cleanup(func() { cmd.Process.Kill(); <-dead })
	status := func(method, at string) int {
		var body bytes.Buffer
		form := multipart.NewWriter(&body)
		form.WriteField("model", os.Args[0]) // a file that exists and is no model: /load would exit on it
		form.Close()
		req, _ := http.NewRequest(method, root+at, &body)
		req.Header.Set("Content-Type", form.FormDataContentType())
		req.Header.Set("Origin", "https://example.invalid")
		resp, err := (&http.Client{Timeout: 5 * time.Second}).Do(req)
		if err != nil {
			return -1
		}
		resp.Body.Close()
		return resp.StatusCode
	}
	for ready := time.Now().Add(2 * time.Minute); status(http.MethodGet, path+"/health") != http.StatusOK; time.Sleep(250 * time.Millisecond) {
		select {
		case <-dead:
			t.Fatalf("%s stopped before it was ready: %v", filepath.Base(server), cmd.ProcessState)
		default:
		}
		if time.Now().After(ready) {
			t.Fatalf("%s did not answer under its path within 2 min", filepath.Base(server))
		}
	}
	for _, c := range []struct{ method, at string }{
		{http.MethodPost, "/inference"}, {http.MethodPost, "/load"}, {http.MethodGet, "/health"}, {http.MethodGet, "/"},
	} {
		if got := status(c.method, c.at); got != http.StatusNotFound {
			t.Errorf("%s %s at the root: %d, want 404", c.method, c.at, got)
		}
	}
	select {
	case <-dead:
		t.Fatalf("%s stopped after the requests at the root: %v", filepath.Base(server), cmd.ProcessState)
	case <-time.After(500 * time.Millisecond):
	}
	if got := status(http.MethodGet, path+"/health"); got != http.StatusOK {
		t.Fatalf("under its path after them: %d", got)
	}
}

// The ears' path is a secret, and the ears' errors travel: the failed servers and the reason for falling back go to
// the journal and to stderr, and a transcription that failed goes back to the device that posted to /hear. None names
// the path; each keeps the server, the address and the cause, for the diagnosis.
func TestTheEarsPathStaysOutOfTheirMessages(t *testing.T) {
	t.Setenv("BRAIN_FAKE_WHISPER", "1")
	deaf, dies, cpu := fakeWhisperExe(t, "deaf"), fakeWhisperExe(t, "dies"), fakeWhisperExe(t, "cpu")
	start := func(servers ...string) (e *ears, addr, path string) {
		port := freePort()
		e, err := startEars(servers, "model.bin", "fr", port, 1, "", 0)
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(e.close)
		addr = "127.0.0.1:" + strconv.Itoa(port)
		return e, addr, strings.TrimPrefix(e.url, "http://"+addr)
	}

	// deaf stops at its warm-up, then dies hears it and stops at the first sentence, which cpu hears.
	e, addr, path := start(deaf, dies, cpu)
	readyLine, readyNote := earsReady(e)
	var why string
	e.fell = func(_, w string) { why = w }
	if text, err := e.hear(make([]float32, 1600)); text != "cpu" || err != nil {
		t.Fatalf("%q %v", text, err)
	}
	fellLine, fellNote := earsFell(cpu, why)

	// A server alone that dies: the sentence fails, and /hear says why.
	lone, loneAddr, lonePath := start(dies)
	r := &remote{secret: "s3cret", route: func(utterance) (int, string) { return 1, "channel" }}
	r.hearWith(lone.hearIn)
	req, _ := http.NewRequest(http.MethodPost, remoteServer(t, r).URL+"/hear", strings.NewReader(string(wavBytes(t, earsRate, 1))))
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, "s3cret")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if resp.StatusCode != http.StatusBadGateway {
		t.Fatalf("/hear with ears that died: %d", resp.StatusCode)
	}

	for _, c := range []struct{ what, msg, path string }{
		{"the failed servers (e.failed)", strings.Join(e.failed, "; "), path},
		{"the journal's ears note at start", fmt.Sprint(readyNote), path},
		{"stderr's ready line", readyLine, path},
		{"the journal's ears note on falling back", fmt.Sprint(fellNote), path},
		{"stderr's falling-back line", fellLine, path},
		{"/hear's 502", string(body), lonePath},
	} {
		if strings.Contains(c.msg, c.path[1:]) {
			t.Errorf("%s names the ears' path: %s", c.what, strings.ReplaceAll(c.msg, c.path[1:], "<path>"))
		}
	}
	for _, c := range []struct{ what, msg string }{
		{"the failed servers", strings.Join(e.failed, "; ")},
		{"the falling-back line", fellLine},
		{"the ready line", readyLine},
		{"/hear's 502", string(body)},
	} {
		want := addr
		if c.what == "/hear's 502" {
			want = loneAddr
		}
		if !strings.Contains(c.msg, want) {
			t.Errorf("%s lost the address %s", c.what, want)
		}
	}
	if !strings.Contains(strings.Join(e.failed, "; "), "deaf.exe") || !strings.Contains(fellLine, "dies.exe died") ||
		!strings.Contains(readyLine, "dies.exe") || !strings.Contains(string(body), "transcription:") {
		t.Errorf("a message lost its server or cause: failed %q; fell %q; ready %q; 502 %q",
			strings.ReplaceAll(strings.Join(e.failed, "; "), path[1:], "<path>"), strings.ReplaceAll(fellLine, path[1:], "<path>"),
			strings.ReplaceAll(readyLine, path[1:], "<path>"), strings.ReplaceAll(string(body), lonePath[1:], "<path>"))
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
