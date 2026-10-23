package main

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestThinkFilterAcrossChunks(t *testing.T) {
	var f thinkFilter
	var out strings.Builder
	for _, chunk := range []string{"<thi", "nk>I should greet.</th", "ink>\n\nBon", "jour <b>!"} {
		out.WriteString(f.feed(chunk))
	}
	if got := out.String(); got != "\n\nBonjour <b>!" {
		t.Fatalf("%q", got)
	}
}

func TestSentencesComeOutAsTheyEnd(t *testing.T) {
	var s sentences
	var got []string
	for _, token := range []string{"Bonjour, je suis Del", "ta. Je dirige l'usine ! O", "k. Que veux-tu construire", " aujourd'hui ?"} {
		got = append(got, s.feed(token)...)
	}
	got = append(got, s.flush())
	// "Ok." is too short to say on its own: it waits for the next sentence.
	want := []string{"Bonjour, je suis Delta.", "Je dirige l'usine !", "Ok. Que veux-tu construire aujourd'hui ?"}
	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Fatalf("got %q", got)
	}
}

func TestChatStreamsTokens(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		if !strings.Contains(string(body), `"think":false`) || !strings.Contains(string(body), `"stream":true`) {
			t.Errorf("request %s", body)
		}
		for _, c := range []string{"Bon", "jour", " !"} {
			fmt.Fprintf(w, `{"message":{"role":"assistant","content":%q},"done":false}`+"\n", c)
		}
		io.WriteString(w, `{"message":{"role":"assistant","content":""},"done":true}`+"\n")
	}))
	defer server.Close()
	var got []string
	if err := chat(context.Background(), server.URL, "m", []message{{"user", "salut"}}, func(s string) { got = append(got, s) }); err != nil {
		t.Fatal(err)
	}
	if strings.Join(got, "|") != "Bon|jour| !" {
		t.Fatalf("%q", got)
	}
}

func TestWavEnvelope(t *testing.T) {
	// 0.1 s at 1000 Hz: 50 ms of silence, then 50 ms at half scale.
	samples := make([]float32, 100)
	for i := 50; i < 100; i++ {
		samples[i] = 0.5
	}
	path := filepath.Join(t.TempDir(), "a.wav")
	if err := writeWav(path, 1000, samples); err != nil {
		t.Fatal(err)
	}
	w, err := readWav(path)
	if err != nil {
		t.Fatal(err)
	}
	if w.seconds != 0.1 || len(w.envelope) != 5 || w.envelope[0] != 0 || w.envelope[4] != 0.5 {
		t.Fatalf("%+v", w)
	}
}

// A fake Piper: the test binary itself, run with BRAIN_FAKE_PIPER set. For each line it writes a WAV into
// --output_dir and prints its path, as Piper does. With BRAIN_FAKE_WHISPER set, it is a fake whisper-server instead
// (ears_test.go).
func TestMain(m *testing.M) {
	if os.Getenv("BRAIN_FAKE_WHISPER") != "" {
		fakeWhisper()
	}
	if os.Getenv("BRAIN_FAKE_PIPER") == "" {
		os.Exit(m.Run())
	}
	dir := ""
	for i, a := range os.Args {
		if a == "--output_dir" {
			dir = os.Args[i+1]
		}
	}
	in := bufio.NewScanner(os.Stdin)
	for n := 0; in.Scan(); n++ {
		path := filepath.Join(dir, fmt.Sprintf("%d.wav", n))
		writeWav(path, 1000, make([]float32, 20*len(in.Text())))
		fmt.Println(path)
	}
	os.Exit(0)
}

func TestVoiceKeepsOneProcess(t *testing.T) {
	t.Setenv("BRAIN_FAKE_PIPER", "1")
	v, err := startVoice(os.Args[0], "model.onnx", t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer v.close()
	for i, line := range []string{"Bonjour.", "Je suis   Delta,\nle robot."} {
		path, err := v.say(line)
		if err != nil {
			t.Fatal(err)
		}
		w, err := readWav(path)
		if err != nil || !strings.HasSuffix(path, fmt.Sprintf("%d.wav", i)) {
			t.Fatalf("%s: %v", path, err)
		}
		// The line reaches Piper on one line, its spaces folded.
		if want := float64(20*len(strings.Join(strings.Fields(line), " "))) / 1000; w.seconds != want {
			t.Fatalf("%s: %v s, want %v", line, w.seconds, want)
		}
	}
}
