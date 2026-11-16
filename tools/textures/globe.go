package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"image"
	"image/jpeg"
	"io"
	"math"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// The globes the scene's modules may paint their triangles with (Gaia's planet: "earth"). NASA's Blue Marble: Next
// Generation, with topography and bathymetry, one month of 2004, 21600 × 10800 (about 2 km a pixel), equirectangular
// from 180° W at the left. NASA asks that it be credited to NASA Earth Observatory.
//
//	<out>/globes/earth/west.png  180° W to 0°, 4096 × 4096, sRGB
//	<out>/globes/earth/east.png  0° to 180° E
//	<out>/globes/earth/globe.json  its source, its credit and the SHA-256 of the download
//
// The two halves are square so that the scene keeps them as two layers of one mipmapped image, 8192 × 4096 together.
const (
	globeMonth = "july"
	globeFile  = "world.topo.bathy.200407.3x21600x10800.jpg"
	globeURL   = "https://assets.science.nasa.gov/content/dam/science/esd/eo/images/bmng/bmng-topography-bathymetry/" + globeMonth + "/" + globeFile
	globeHalf  = 4096
)

type globeInfo struct {
	Name   string `json:"name"`
	Source string `json:"source"`
	Credit string `json:"credit"`
	Size   string `json:"size"`
	SHA256 string `json:"sha256"`
}

func globe(src, out string) error {
	from := filepath.Join(src, "bmng", globeFile)
	if _, err := os.Stat(from); err != nil {
		if err := download(globeURL, from); err != nil {
			return fmt.Errorf("earth: %v", err)
		}
	}
	sum, err := fileSum(from)
	if err != nil {
		return err
	}
	f, err := os.Open(from)
	if err != nil {
		return err
	}
	img, err := jpeg.Decode(f)
	f.Close()
	if err != nil {
		return fmt.Errorf("%s: %v", from, err)
	}
	b := img.Bounds()
	if b.Dx() != 2*b.Dy() {
		return fmt.Errorf("%s: %d × %d is not equirectangular (2:1)", from, b.Dx(), b.Dy())
	}
	px := shrinkGlobe(img, 2*globeHalf, globeHalf)
	dir := filepath.Join(out, "globes", "earth")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	for i, name := range []string{"west.png", "east.png"} {
		half := image.NewNRGBA(image.Rect(0, 0, globeHalf, globeHalf))
		for y := 0; y < globeHalf; y++ {
			for x := 0; x < globeHalf; x++ {
				c := px[y*2*globeHalf+i*globeHalf+x]
				o := half.PixOffset(x, y)
				half.Pix[o], half.Pix[o+1], half.Pix[o+2], half.Pix[o+3] = srgb8(c[0]), srgb8(c[1]), srgb8(c[2]), 255
			}
		}
		if err := save(filepath.Join(dir, name), opaqueRGB(half)); err != nil {
			return err
		}
	}
	info := globeInfo{Name: "earth", Source: globeURL, Credit: "NASA Earth Observatory (Blue Marble: Next Generation, July 2004)",
		Size: fmt.Sprintf("%d × %d in two halves", 2*globeHalf, globeHalf), SHA256: sum}
	body, _ := json.MarshalIndent(info, "", "  ")
	fmt.Printf("textures: earth from %s, %d × %d\n", globeFile, 2*globeHalf, globeHalf)
	return os.WriteFile(filepath.Join(dir, "globe.json"), append(body, '\n'), 0o644)
}

// opaqueRGB: the image as 8-bit RGB, which the scene's PNG reader takes and is a quarter smaller than RGBA.
func opaqueRGB(img *image.NRGBA) image.Image { return rgbImage{img} }

// rgbImage reports itself opaque so that image/png writes it as 8-bit RGB.
type rgbImage struct{ *image.NRGBA }

func (rgbImage) Opaque() bool { return true }

func download(url, to string) error {
	if err := os.MkdirAll(filepath.Dir(to), 0o755); err != nil {
		return err
	}
	client := &http.Client{Timeout: 20 * time.Minute}
	resp, err := client.Get(url)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("%s: %s", url, resp.Status)
	}
	part := to + ".part"
	f, err := os.Create(part)
	if err != nil {
		return err
	}
	if _, err := io.Copy(f, resp.Body); err != nil {
		f.Close()
		return err
	}
	if err := f.Close(); err != nil {
		return err
	}
	return os.Rename(part, to)
}

func fileSum(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// shrinkGlobe: the image at tw × th in linear light, each new pixel the average of the old ones it covers, parts of
// pixels weighed by how much of them it covers. Each target row reads only the few source rows under it, so the whole
// source is never held in floats.
func shrinkGlobe(img image.Image, tw, th int) [][3]float32 {
	var lut [256]float32
	for i := range lut {
		c := float64(i) / 255
		if c <= 0.04045 {
			lut[i] = float32(c / 12.92)
		} else {
			lut[i] = float32(math.Pow((c+0.055)/1.055, 2.4))
		}
	}
	b := img.Bounds()
	w, h := b.Dx(), b.Dy()
	type part struct {
		i int
		w float32
	}
	spans := func(from, to int) [][]part {
		out := make([][]part, to)
		scale := float64(from) / float64(to)
		for t := 0; t < to; t++ {
			lo, hi := float64(t)*scale, float64(t+1)*scale
			for i := int(lo); i < int(math.Ceil(hi)) && i < from; i++ {
				if cover := math.Min(hi, float64(i+1)) - math.Max(lo, float64(i)); cover > 0 {
					out[t] = append(out[t], part{i, float32(cover / scale)})
				}
			}
		}
		return out
	}
	sx, sy := spans(w, tw), spans(h, th)
	out := make([][3]float32, tw*th)
	var wg sync.WaitGroup
	for k := 0; k < 16; k++ {
		wg.Add(1)
		go func(k int) {
			defer wg.Done()
			row := make([][3]float32, w)
			for t := k; t < th; t += 16 {
				acc := out[t*tw : (t+1)*tw]
				for _, sv := range sy[t] {
					for x := 0; x < w; x++ {
						r, g, bl, _ := img.At(b.Min.X+x, b.Min.Y+sv.i).RGBA()
						row[x] = [3]float32{lut[r>>8], lut[g>>8], lut[bl>>8]}
					}
					for u, sp := range sx {
						for _, s := range sp {
							p, f := row[s.i], s.w*sv.w
							acc[u][0], acc[u][1], acc[u][2] = acc[u][0]+p[0]*f, acc[u][1]+p[1]*f, acc[u][2]+p[2]*f
						}
					}
				}
			}
		}(k)
	}
	wg.Wait()
	return out
}
func srgb8(v float32) byte {
	c := float64(min(max(v, 0), 1))
	if c <= 0.0031308 {
		c *= 12.92
	} else {
		c = 1.055*math.Pow(c, 1/2.4) - 0.055
	}
	return byte(min(max(c*255+0.5, 0), 255))
}
