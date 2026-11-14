package main

import (
	"encoding/json"
	"fmt"
	"image/color"
	"image/png"
	"os"
	"sort"
)

// brick is a whole brick of a scan, in the scan's 0..1 coordinates with y going down.
type brick struct {
	X0, Y0, X1, Y1 float64
	Mean           float64 // its mean height, 0..1
}

// findBricks finds the whole bricks of a brick-wall scan from its height map, where the mortar lies lower: the regions
// above Otsu's threshold, eroded by a few pixels to part bricks that touch, kept when they are brick-shaped and not
// cut by the scan's edge. It writes the threshold and the bricks as JSON to out, for wall.py.
func findBricks(heightPath, out string) (int, error) {
	f, err := os.Open(heightPath)
	if err != nil {
		return 0, err
	}
	img, err := png.Decode(f)
	f.Close()
	if err != nil {
		return 0, fmt.Errorf("%s: %v", heightPath, err)
	}
	b := img.Bounds()
	w, h := b.Dx(), b.Dy()
	hv := make([]float64, w*h)
	hist := make([]int, 256)
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			v := float64(color.Gray16Model.Convert(img.At(b.Min.X+x, b.Min.Y+y)).(color.Gray16).Y) / 65535
			hv[y*w+x] = v
			hist[min(int(v*256), 255)]++
		}
	}
	thr := float64(otsu(hist, w*h)) / 256
	mask := make([]bool, w*h)
	for i, v := range hv {
		mask[i] = v > thr
	}
	const e = 6 // pixels eroded, then given back to each brick's rectangle
	er := erode(erode(mask, w, h, e, 1), w, h, e, w)
	label := make([]bool, w*h)
	var found []brick
	stack := make([]int, 0, 1<<16)
	for i := range er {
		if !er[i] || label[i] {
			continue
		}
		label[i] = true
		stack = append(stack[:0], i)
		x0, y0, x1, y1, n, sum := w, h, -1, -1, 0, 0.0
		for len(stack) > 0 {
			p := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			x, y := p%w, p/w
			n++
			sum += hv[p]
			x0, y0, x1, y1 = min(x0, x), min(y0, y), max(x1, x), max(y1, y)
			for _, q := range [4]int{p - 1, p + 1, p - w, p + w} {
				if q < 0 || q >= w*h || (q == p-1 && x == 0) || (q == p+1 && x == w-1) || !er[q] || label[q] {
					continue
				}
				label[q] = true
				stack = append(stack, q)
			}
		}
		fill := float64(n) / float64((x1-x0+1)*(y1-y0+1))
		x0, y0, x1, y1 = x0-e, y0-e, x1+e, y1+e
		bw, bh := x1-x0+1, y1-y0+1
		if x0 <= 2 || y0 <= 2 || x1 >= w-3 || y1 >= h-3 || bw < w/9 || bw > w/3 || bh < h/30 || bh > h/9 || fill < 0.75 {
			continue
		}
		found = append(found, brick{float64(x0) / float64(w), float64(y0) / float64(h), float64(x1+1) / float64(w), float64(y1+1) / float64(h), sum / float64(n)})
	}
	sort.Slice(found, func(i, j int) bool {
		if found[i].Y0 != found[j].Y0 {
			return found[i].Y0 < found[j].Y0
		}
		return found[i].X0 < found[j].X0
	})
	body, _ := json.MarshalIndent(map[string]any{"threshold": thr, "width": w, "height": h, "bricks": found}, "", " ")
	return len(found), os.WriteFile(out, body, 0o644)
}

func otsu(hist []int, total int) int {
	var sum float64
	for i, c := range hist {
		sum += float64(i * c)
	}
	var sumB, wB, best float64
	t := 0
	for i, c := range hist {
		wB += float64(c)
		wF := float64(total) - wB
		if wB == 0 || wF == 0 {
			continue
		}
		sumB += float64(i * c)
		mB, mF := sumB/wB, (sum-sumB)/wF
		if v := wB * wF * (mB - mF) * (mB - mF); v > best {
			best, t = v, i
		}
	}
	return t
}

// erode keeps a pixel when every pixel within r of it along one axis (step 1 across, w down) is kept.
func erode(in []bool, w, h, r, step int) []bool {
	out := make([]bool, len(in))
	for p := range in {
		x, y := p%w, p/w
		ok := true
		for d := -r; d <= r && ok; d++ {
			if step == 1 {
				ok = x+d >= 0 && x+d < w && in[p+d]
			} else {
				ok = y+d >= 0 && y+d < h && in[p+d*w]
			}
		}
		out[p] = ok
	}
	return out
}
