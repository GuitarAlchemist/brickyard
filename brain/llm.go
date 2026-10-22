package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
)

type message struct {
	Role    string `json:"role"`
	Content string `json:"content"`
}

// chat streams a local Ollama model's answer, token by token, without its reasoning.
func chat(ctx context.Context, url, model string, messages []message, token func(string)) error {
	body, _ := json.Marshal(map[string]any{
		"model": model, "messages": messages, "stream": true, "think": false, "keep_alive": "5m",
		// A short context: the model's default one took 10 GB of video memory beside the live scene.
		"options": map[string]any{"temperature": 0.6, "num_predict": 160, "num_ctx": 4096},
	})
	req, err := http.NewRequestWithContext(ctx, "POST", url+"/api/chat", bytes.NewReader(body))
	if err != nil {
		return err
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		text, _ := io.ReadAll(io.LimitReader(resp.Body, 2000))
		return fmt.Errorf("ollama: %s: %s", resp.Status, bytes.TrimSpace(text))
	}
	var thinking thinkFilter
	lines := bufio.NewScanner(resp.Body)
	lines.Buffer(make([]byte, 64*1024), 1024*1024)
	for lines.Scan() {
		var chunk struct {
			Message message `json:"message"`
			Done    bool    `json:"done"`
			Error   string  `json:"error"`
		}
		if err := json.Unmarshal(lines.Bytes(), &chunk); err != nil {
			return fmt.Errorf("ollama: %w", err)
		}
		if chunk.Error != "" {
			return fmt.Errorf("ollama: %s", chunk.Error)
		}
		if text := thinking.feed(chunk.Message.Content); text != "" {
			token(text)
		}
		if chunk.Done {
			return nil
		}
	}
	return lines.Err()
}

// thinkFilter drops a <think>...</think> block, for models or servers that ignore "think": false.
type thinkFilter struct {
	inside bool
	held   string
}

func (f *thinkFilter) feed(s string) string {
	s = f.held + s
	f.held = ""
	var out strings.Builder
	for s != "" {
		tag := "<think>"
		if f.inside {
			tag = "</think>"
		}
		i := strings.Index(s, tag)
		if i < 0 {
			// Hold back what could be the start of a tag split across chunks.
			keep := 0
			for k := min(len(tag)-1, len(s)); k > 0; k-- {
				if strings.HasPrefix(tag, s[len(s)-k:]) {
					keep = k
					break
				}
			}
			if !f.inside {
				out.WriteString(s[:len(s)-keep])
			}
			f.held = s[len(s)-keep:]
			break
		}
		if !f.inside {
			out.WriteString(s[:i])
		}
		s = s[i+len(tag):]
		f.inside = !f.inside
	}
	return out.String()
}

// sentences cuts a stream of text into sentences, so that each can be spoken as soon as it ends.
type sentences struct{ buf strings.Builder }

func (s *sentences) feed(text string) []string {
	var out []string
	for _, r := range text {
		s.buf.WriteRune(r)
		if r == '\n' || ((r == ' ') && endsSentence(s.buf.String())) {
			if t := strings.TrimSpace(s.buf.String()); hasWords(t) {
				out = append(out, t)
			}
			s.buf.Reset()
		}
	}
	return out
}

func (s *sentences) flush() string {
	t := strings.TrimSpace(s.buf.String())
	s.buf.Reset()
	if hasWords(t) {
		return t
	}
	return ""
}

// endsSentence: the text ends with a sentence mark and a space, and is long enough to be worth a breath.
func endsSentence(t string) bool {
	t = strings.TrimRight(t, " ")
	if len([]rune(t)) < 12 {
		return false
	}
	return strings.HasSuffix(t, ".") || strings.HasSuffix(t, "!") || strings.HasSuffix(t, "?") ||
		strings.HasSuffix(t, "…") || strings.HasSuffix(t, ":") || strings.HasSuffix(t, ";")
}

func hasWords(t string) bool {
	for _, r := range t {
		if r >= '0' && r <= '9' || r >= 'A' && r <= 'Z' || r >= 'a' && r <= 'z' || r > 127 {
			return true
		}
	}
	return false
}
