package main

import (
	"bufio"
	"context"
	"encoding/binary"
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
	for _, token := range []string{"Bonjour, je suis Pin", "ion. Je dirige l'usine ! O", "k. Que veux-tu construire", " aujourd'hui ?"} {
		got = append(got, s.feed(token)...)
	}
	got = append(got, s.flush())
	// "Ok." is too short to say on its own: it waits for the next sentence.
	want := []string{"Bonjour, je suis Pinion.", "Je dirige l'usine !", "Ok. Que veux-tu construire aujourd'hui ?"}
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

func writeWav(t *testing.T, path string, rate int, samples []int16) {
	t.Helper()
	data := make([]byte, 44+2*len(samples))
	copy(data, "RIFF")
	binary.LittleEndian.PutUint32(data[4:], uint32(36+2*len(samples)))
	copy(data[8:], "WAVEfmt ")
	binary.LittleEndian.PutUint32(data[16:], 16)
	binary.LittleEndian.PutUint16(data[20:], 1)
	binary.LittleEndian.PutUint16(data[22:], 1)
	binary.LittleEndian.PutUint32(data[24:], uint32(rate))
	binary.LittleEndian.PutUint32(data[28:], uint32(rate*2))
	binary.LittleEndian.PutUint16(data[32:], 2)
	binary.LittleEndian.PutUint16(data[34:], 16)
	copy(data[36:], "data")
	binary.LittleEndian.PutUint32(data[40:], uint32(2*len(samples)))
	for i, s := range samples {
		binary.LittleEndian.PutUint16(data[44+2*i:], uint16(s))
	}
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestWavEnvelope(t *testing.T) {
	// 0.1 s at 1000 Hz: 50 ms of silence, then 50 ms at half scale.
	samples := make([]int16, 100)
	for i := 50; i < 100; i++ {
		samples[i] = 16384
	}
	path := filepath.Join(t.TempDir(), "a.wav")
	writeWav(t, path, 1000, samples)
	w, err := readWav(path)
	if err != nil {
		t.Fatal(err)
	}
	if w.seconds != 0.1 || len(w.envelope) != 5 || w.envelope[0] != 0 || w.envelope[4] != 0.5 {
		t.Fatalf("%+v", w)
	}
}

// A fake Piper: the test binary itself, run with BRAIN_FAKE_PIPER set. For each line it writes a WAV into
// --output_dir and prints its path, as Piper does.
func TestMain(m *testing.M) {
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
		writeWav(&testing.T{}, path, 1000, make([]int16, 20*len(in.Text())))
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
	for i, line := range []string{"Bonjour.", "Je suis   Pinion,\nle robot."} {
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
