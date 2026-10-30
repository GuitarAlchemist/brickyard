package main

import (
	"context"
	"encoding/json"
	"maps"
	"net/http"
	"net/http/httptest"
	"os"
	"slices"
	"strings"
	"testing"
)

// apiPath is where the brain's HTTP contract lives, from brain/.
const apiPath = "../docs/brain-api.json"

// operations are the keys of an OpenAPI path item that name a method.
var operations = []string{"get", "put", "post", "delete", "options", "head", "patch", "trace", "query"}

// pathList is a router that lists the paths registered on it, in order.
type pathList []string

// HandleFunc lists the pattern, and drops its handler.
func (p *pathList) HandleFunc(pattern string, _ func(http.ResponseWriter, *http.Request)) {
	*p = append(*p, pattern)
}

// readAPI parses docs/brain-api.json, as a tree of maps.
func readAPI(t *testing.T) map[string]any {
	t.Helper()
	data, err := os.ReadFile(apiPath)
	if err != nil {
		t.Fatal(err)
	}
	var doc map[string]any
	if err := json.Unmarshal(data, &doc); err != nil {
		t.Fatalf("%s: %v", apiPath, err)
	}
	return doc
}

// documented is each documented path with its methods, upper case.
func documented(t *testing.T, doc map[string]any) map[string][]string {
	t.Helper()
	paths, _ := doc["paths"].(map[string]any)
	if len(paths) == 0 {
		t.Fatalf("%s documents no path", apiPath)
	}
	methods := map[string][]string{}
	for path, item := range paths {
		ops, _ := item.(map[string]any)
		for key := range ops {
			if slices.Contains(operations, key) {
				methods[path] = append(methods[path], strings.ToUpper(key))
			}
		}
		if len(methods[path]) == 0 {
			t.Errorf("%s: no operation", path)
		}
	}
	return methods
}

// The contract documents every path the brain's mux answers, and no other.
func TestTheContractNamesEveryPathTheBrainAnswers(t *testing.T) {
	doc := readAPI(t)
	if doc["openapi"] != "3.2.0" {
		t.Errorf("openapi %v, want 3.2.0", doc["openapi"])
	}
	if info, _ := doc["info"].(map[string]any); info["version"] == "" || info["version"] == nil {
		t.Error("the contract has no info.version")
	}
	var registered pathList
	h := &hooks{remote: &remote{asks: newAsks()}} // the voice brain's: a gateway, and requests to Gaia
	h.routes(&registered)
	got := slices.Sorted(slices.Values(registered))
	want := slices.Sorted(maps.Keys(documented(t, doc)))
	if !slices.Equal(got, want) {
		t.Errorf("the brain registers %v; %s documents %v", got, apiPath, want)
	}
}

// Each path takes the methods the contract gives it, and refuses the others with 403, as remote.allowed does.
func TestTheContractNamesEachPathsMethod(t *testing.T) {
	h := &hooks{logf: t.Logf, remote: &remote{secret: "s3cret", asks: newAsks()}}
	srv := h.handler()
	ctx, cancel := context.WithCancel(context.Background())
	cancel() // a stream answers its status, then ends with the request
	probe := func(method, path string) int {
		req := httptest.NewRequestWithContext(ctx, method, path, strings.NewReader(""))
		req.Host = "127.0.0.1:8807"
		req.Header.Set(hookHeader, "1")
		req.Header.Set(remoteHeader, "s3cret")
		w := httptest.NewRecorder()
		srv.ServeHTTP(w, req)
		return w.Code
	}
	for path, methods := range documented(t, readAPI(t)) {
		for _, method := range []string{http.MethodGet, http.MethodPost} {
			status := probe(method, path)
			switch {
			case slices.Contains(methods, method) && (status == http.StatusForbidden || status == http.StatusNotFound):
				t.Errorf("%s %s: %d, though documented", method, path, status)
			case !slices.Contains(methods, method) && status != http.StatusForbidden:
				t.Errorf("%s %s: %d, want 403 for an undocumented method", method, path, status)
			}
		}
	}
}

// Every $ref resolves, and the enums the contract lists are the code's own.
func TestTheContractsSchemasHoldTogether(t *testing.T) {
	doc := readAPI(t)
	var walk func(node any)
	walk = func(node any) {
		switch n := node.(type) {
		case map[string]any:
			if ref, ok := n["$ref"].(string); ok && resolve(doc, ref) == nil {
				t.Errorf("$ref %s resolves to nothing", ref)
			}
			for _, v := range n {
				walk(v)
			}
		case []any:
			for _, v := range n {
				walk(v)
			}
		}
	}
	walk(doc)

	enum := func(schema, property string) []string {
		s, _ := resolve(doc, "#/components/schemas/"+schema+"/properties/"+property).(map[string]any)
		values, _ := s["enum"].([]any)
		var out []string
		for _, v := range values {
			out = append(out, v.(string))
		}
		return slices.Sorted(slices.Values(out))
	}
	for _, c := range []struct {
		schema, property string
		code             []string
	}{
		{"Gesture", "gesture", slices.Collect(maps.Keys(gestures))},
		{"AskRequest", "to", askable},
		{"AnswerRequest", "mood", append([]string{""}, moods...)},
	} {
		code := slices.Sorted(slices.Values(c.code))
		if got := enum(c.schema, c.property); !slices.Equal(got, code) {
			t.Errorf("%s.%s lists %q; the code takes %q", c.schema, c.property, got, code)
		}
	}
}

// resolve follows a local JSON pointer such as #/components/schemas/Heard through doc, or returns nil.
func resolve(doc map[string]any, ref string) any {
	rest, ok := strings.CutPrefix(ref, "#/")
	if !ok {
		return nil
	}
	var node any = doc
	for _, key := range strings.Split(rest, "/") {
		m, ok := node.(map[string]any)
		if !ok {
			return nil
		}
		node = m[strings.NewReplacer("~1", "/", "~0", "~").Replace(key)]
	}
	return node
}
