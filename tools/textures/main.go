// textures fetches the hall's photographed materials from Poly Haven (CC0) and packs them for the scene.
//
//	go -C tools/textures run . [-src DIR] [-out DIR]
//
// For each material of textures.json it downloads, once, the colour (JPEG), the OpenGL normal map, the AO/roughness/metal
// map ("arm") and the height map at the resolution named, into -src. It then writes into -out (by default
// %LOCALAPPDATA%\delta\textures, where the scene looks) three 8-bit PNGs of the manifest's size, which the scene's own
// PNG reader takes:
//
//	<name>/albedo.png  sRGB colour, the height in alpha (1 at the surface, 0 deepest)
//	<name>/normal.png  the tangent-space normal, OpenGL's convention (green up)
//	<name>/arm.png     ambient occlusion, roughness, metalness
//
// and textures.json beside them: each material's name, its source, its licence, its tile in metres and the SHA-256 of
// each file it was made from. Poly Haven's textures are CC0: https://polyhaven.com/license.
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"image"
	"image/color"
	"image/jpeg"
	"image/png"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"time"
)

type material struct {
	Name      string  `json:"name"`
	PolyHaven string  `json:"polyhaven"`
	Res       string  `json:"res"`
	Metres    float64 `json:"metres"`
}

type manifest struct {
	Size      int        `json:"size"`
	Materials []material `json:"materials"`
}

type packed struct {
	Name    string            `json:"name"`
	Source  string            `json:"source"`
	Licence string            `json:"licence"`
	Metres  float64           `json:"metres"`
	SHA256  map[string]string `json:"sha256"`
}

// The four maps, by the file each is kept in and the key and format Poly Haven's files API gives it.
var maps = []struct{ file, key, format string }{
	{"albedo.jpg", "Diffuse", "jpg"},
	{"normal.png", "nor_gl", "png"},
	{"arm.png", "arm", "png"},
	{"height.png", "Displacement", "png"},
}

func main() {
	cache, _ := os.UserCacheDir()
	src := flag.String("src", filepath.Join(os.TempDir(), "delta-textures-src"), "where the downloads are kept")
	out := flag.String("out", filepath.Join(cache, "delta", "textures"), "where the scene's textures are written")
	list := flag.String("manifest", "textures.json", "the materials")
	flag.Parse()
	if err := run(*list, *src, *out); err != nil {
		fmt.Fprintln(os.Stderr, "textures:", err)
		os.Exit(1)
	}
}

func run(list, src, out string) error {
	raw, err := os.ReadFile(list)
	if err != nil {
		return err
	}
	var m manifest
	if err := json.Unmarshal(raw, &m); err != nil {
		return fmt.Errorf("%s: %v", list, err)
	}
	if m.Size < 16 || m.Size&(m.Size-1) != 0 {
		return fmt.Errorf("size %d is not a power of two of at least 16", m.Size)
	}
	var made []packed
	for _, mat := range m.Materials {
		dir := filepath.Join(src, mat.PolyHaven, mat.Res)
		if err := fetch(mat, dir); err != nil {
			return fmt.Errorf("%s: %v", mat.Name, err)
		}
		p, err := pack(mat, dir, filepath.Join(out, mat.Name), m.Size)
		if err != nil {
			return fmt.Errorf("%s: %v", mat.Name, err)
		}
		made = append(made, p)
		fmt.Printf("textures: %s from %s (%s), a tile of %.2f m\n", mat.Name, mat.PolyHaven, mat.Res, mat.Metres)
	}
	body, _ := json.MarshalIndent(made, "", "  ")
	return os.WriteFile(filepath.Join(out, "textures.json"), append(body, '\n'), 0o644)
}

// fetch downloads the maps that are not in dir yet.
func fetch(mat material, dir string) error {
	missing := false
	for _, f := range maps {
		if _, err := os.Stat(filepath.Join(dir, f.file)); err != nil {
			missing = true
		}
	}
	if !missing {
		return nil
	}
	client := &http.Client{Timeout: 5 * time.Minute}
	get := func(url string) (*http.Response, error) {
		req, err := http.NewRequest(http.MethodGet, url, nil)
		if err != nil {
			return nil, err
		}
		req.Header.Set("User-Agent", "delta-textures")
		resp, err := client.Do(req)
		if err == nil && resp.StatusCode != http.StatusOK {
			resp.Body.Close()
			return nil, fmt.Errorf("%s: %s", url, resp.Status)
		}
		return resp, err
	}
	resp, err := get("https://api.polyhaven.com/files/" + mat.PolyHaven)
	if err != nil {
		return err
	}
	var files map[string]map[string]map[string]struct {
		URL string `json:"url"`
	}
	err = json.NewDecoder(resp.Body).Decode(&files)
	resp.Body.Close()
	if err != nil {
		return fmt.Errorf("Poly Haven's file list: %v", err)
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	for _, f := range maps {
		url := files[f.key][mat.Res][f.format].URL
		if url == "" {
			return fmt.Errorf("Poly Haven has no %s %s at %s", f.key, f.format, mat.Res)
		}
		resp, err := get(url)
		if err != nil {
			return err
		}
		tmp := filepath.Join(dir, f.file+".part")
		file, err := os.Create(tmp)
		if err == nil {
			_, err = io.Copy(file, resp.Body)
			if cerr := file.Close(); err == nil {
				err = cerr
			}
		}
		resp.Body.Close()
		if err != nil {
			return err
		}
		if err := os.Rename(tmp, filepath.Join(dir, f.file)); err != nil {
			return err
		}
	}
	return nil
}

func pack(mat material, dir, out string, size int) (packed, error) {
	p := packed{Name: mat.Name, Source: "https://polyhaven.com/a/" + mat.PolyHaven, Licence: "CC0-1.0", Metres: mat.Metres, SHA256: map[string]string{}}
	images := map[string]image.Image{}
	for _, f := range maps {
		path := filepath.Join(dir, f.file)
		body, err := os.ReadFile(path)
		if err != nil {
			return p, err
		}
		sum := sha256.Sum256(body)
		p.SHA256[f.file] = hex.EncodeToString(sum[:])
		file, _ := os.Open(path)
		var img image.Image
		if f.format == "jpg" {
			img, err = jpeg.Decode(file)
		} else {
			img, err = png.Decode(file)
		}
		file.Close()
		if err != nil {
			return p, fmt.Errorf("%s: %v", f.file, err)
		}
		images[f.file] = img
	}
	albedo, err := shrink(images["albedo.jpg"], size)
	if err != nil {
		return p, err
	}
	height, err := shrink(images["height.png"], size)
	if err != nil {
		return p, err
	}
	normal, err := shrink(images["normal.png"], size)
	if err != nil {
		return p, err
	}
	arm, err := shrink(images["arm.png"], size)
	if err != nil {
		return p, err
	}
	// The height's own range is stretched to [0, 1], so that the deepest point is 0 whatever the scan's scale.
	lo, hi := 1.0, 0.0
	for _, px := range height {
		lo, hi = min(lo, px[0]), max(hi, px[0])
	}
	span := max(hi-lo, 1e-6)
	colour := image.NewNRGBA(image.Rect(0, 0, size, size))
	for i, px := range albedo {
		colour.Pix[4*i], colour.Pix[4*i+1], colour.Pix[4*i+2] = byte8(px[0]), byte8(px[1]), byte8(px[2])
		colour.Pix[4*i+3] = byte8((height[i][0] - lo) / span)
	}
	if err := os.MkdirAll(out, 0o755); err != nil {
		return p, err
	}
	for name, img := range map[string]image.Image{"albedo.png": colour, "normal.png": opaque(normal, size), "arm.png": opaque(arm, size)} {
		if err := save(filepath.Join(out, name), img); err != nil {
			return p, err
		}
	}
	return p, nil
}

// shrink averages square blocks of img down to size × size, each channel in [0, 1].
func shrink(img image.Image, size int) ([][3]float64, error) {
	b := img.Bounds()
	if b.Dx() != b.Dy() || b.Dx()%size != 0 {
		return nil, fmt.Errorf("a %d×%d image does not shrink evenly to %d", b.Dx(), b.Dy(), size)
	}
	f := b.Dx() / size
	out := make([][3]float64, size*size)
	for y := 0; y < size; y++ {
		for x := 0; x < size; x++ {
			var s [3]float64
			for j := 0; j < f; j++ {
				for i := 0; i < f; i++ {
					c := color.NRGBA64Model.Convert(img.At(b.Min.X+x*f+i, b.Min.Y+y*f+j)).(color.NRGBA64)
					s[0] += float64(c.R)
					s[1] += float64(c.G)
					s[2] += float64(c.B)
				}
			}
			n := float64(f*f) * 65535
			out[y*size+x] = [3]float64{s[0] / n, s[1] / n, s[2] / n}
		}
	}
	return out, nil
}

func opaque(px [][3]float64, size int) *image.NRGBA {
	img := image.NewNRGBA(image.Rect(0, 0, size, size))
	for i, c := range px {
		img.Pix[4*i], img.Pix[4*i+1], img.Pix[4*i+2], img.Pix[4*i+3] = byte8(c[0]), byte8(c[1]), byte8(c[2]), 255
	}
	return img
}

func byte8(v float64) byte { return byte(min(max(v*255+0.5, 0), 255)) }

func save(path string, img image.Image) error {
	tmp := path + ".part"
	file, err := os.Create(tmp)
	if err != nil {
		return err
	}
	err = (&png.Encoder{CompressionLevel: png.BestCompression}).Encode(file, img)
	if cerr := file.Close(); err == nil {
		err = cerr
	}
	if err != nil {
		return errors.Join(err, os.Remove(tmp))
	}
	return os.Rename(tmp, path)
}
