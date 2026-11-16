package main

import "math"

// Points of the square, named as the diagrams name them. The paper's coloured side is its front (+z); A is the
// bottom-left corner, B bottom-right, D top-right, E top-left, C the centre; the crane's wings come from A and D, its
// neck and tail from E and B.
var (
	A, B, D, E = pt{0, 0}, pt{1, 0}, pt{1, 1}, pt{0, 1}
	C          = pt{0.5, 0.5}
	mL, mR     = pt{0, 0.5}, pt{1, 0.5}
	mB, mT     = pt{0.5, 0}, pt{0.5, 1}
	k          = 0.5 - (1 - math.Sqrt2/2) // where the kites meet the midlines: 0.5 - 0.2929
	// The kites' tops on the midlines, round C.
	kL, kB, kR, kT = pt{k, 0.5}, pt{0.5, k}, pt{1 - k, 0.5}, pt{0.5, 1 - k}
)

// The creases the steps make beyond the final crane's: the midlines whole (the square base folds them all; the bird
// base opens their ends again), and the square round C that folding the base's top down marks in every layer.
var extra = []segment{
	{mL, mR, ""}, {mB, mT, ""},
	{kL, kB, ""}, {kB, kR, ""}, {kR, kT, ""}, {kT, kL, ""},
}

type seg struct{ A, B pt }

// A step: what the diagram says, and the creases folded once it is done.
type step struct {
	Fr, En string
	Folds  []seg
}

// The mountains and valleys a step's diagram names, by the step's index, where the final crane's would mislead: a
// reverse fold turns the spine beyond it inside out, which the faces moving together would otherwise keep. The neck's
// spine to E turns again with the head, so before the head it is the final crane's other way.
var hints = map[int][]hintSeg{
	5:  {{B, E, "V"}},
	7:  {{B, E, "V"}, {mL, mR, "M"}, {mB, mT, "M"}},
	17: {{B, pt{0.6464, 0.3536}, "M"}},
	18: {{pt{0.3536, 0.6464}, pt{0.1279, 0.8721}, "M"}, {pt{0.1279, 0.8721}, E, "M"}},
	19: {{pt{0.1279, 0.8721}, E, "V"}},
}

type hintSeg struct {
	A, B pt
	As   string
}

func union(sets ...[]seg) []seg {
	var out []seg
	for _, s := range sets {
		out = append(out, s...)
	}
	return out
}

func without(set []seg, drop ...seg) []seg {
	var out []seg
	for _, s := range set {
		keep := true
		for _, d := range drop {
			if s == d {
				keep = false
			}
		}
		if keep {
			out = append(out, s)
		}
	}
	return out
}

var (
	squareBase = []seg{{mL, kL}, {kL, C}, {C, kR}, {kR, mR}, {mB, kB}, {kB, C}, {C, kT}, {kT, mT}, {B, E}}
	kitesFront = []seg{{A, kL}, {kL, E}, {A, kB}, {kB, B}}
	topFront   = []seg{{kL, kB}, {kB, kR}, {kR, kT}, {kT, kL}}
	petalFront = union(without(squareBase, seg{mL, kL}, seg{mB, kB}), []seg{{kL, kB}}, kitesFront)
	kitesBack  = []seg{{D, kT}, {kT, E}, {D, kR}, {kR, B}}
	birdBase   = union(without(petalFront, seg{kT, mT}, seg{kR, mR}), []seg{{kT, kR}}, kitesBack)
)

var steps = []step{
	{"La feuille carrée, côté couleur dessus", "The square sheet, coloured side up", nil},
	{"Plier en deux", "Fold in half", []seg{{mL, mR}}},
	{"Déplier", "Unfold", nil},
	{"Plier en deux dans l'autre sens", "Fold in half the other way", []seg{{mB, mT}}},
	{"Déplier", "Unfold", nil},
	{"Plier en triangle", "Fold into a triangle", []seg{{B, E}}},
	{"Déplier", "Unfold", nil},
	{"Rabattre en base carrée", "Collapse into the square base", squareBase},
	{"Plier les bords au centre", "Fold the edges to the centre", union(squareBase, kitesFront)},
	{"Rabattre le haut", "Fold the top down", union(squareBase, kitesFront, topFront)},
	{"Déplier", "Unfold", squareBase},
	{"Pli pétale", "Petal fold", petalFront},
	{"Plier les bords au centre, derrière", "Fold the edges to the centre, behind", union(petalFront, kitesBack)},
	{"Déplier", "Unfold", petalFront},
	{"Pli pétale, derrière : la base de l'oiseau", "Petal fold behind: the bird base", birdBase},
	{"Affiner les pattes devant", "Narrow the legs in front", union(birdBase, narrowFront)},
	{"Affiner les pattes derrière", "Narrow the legs behind", union(birdBase, narrowFront, narrowBack)},
	{"Pli renversé : la queue", "Inside reverse fold: the tail", union(birdBase, narrowFront, narrowBack, tail)},
	{"Pli renversé : le cou", "Inside reverse fold: the neck", union(birdBase, narrowFront, narrowBack, tail, neck)},
	{"Pli renversé : la tête", "Inside reverse fold: the head", union(birdBase, narrowFront, narrowBack, tail, neck, head)},
	{"Rabattre l'aile", "Fold the wing down", union(birdBase, narrowFront, narrowBack, tail, neck, head, wing)},
}

// The creases after the bird base, as polylines through the layers they cut (points given to 4 places, snapped to the
// sheet's vertices): each narrowing is a loop round a leg's four layers.
var (
	narrowFront = union(
		poly(B, pt{0.5, 0.3341}, pt{0.4239, 0.2832}, pt{0.3730, 0.2071}, pt{0.3244, 0.1344}, B),
		poly(E, pt{0.3341, 0.5}, pt{0.2832, 0.4239}, pt{0.2071, 0.3730}, pt{0.1344, 0.3244}, E))
	narrowBack = union(
		poly(B, pt{0.6659, 0.5}, pt{0.7168, 0.5761}, pt{0.8656, 0.6756}, B),
		poly(E, pt{0.5, 0.6659}, pt{0.5761, 0.7168}, pt{0.6756, 0.8656}, E))
	tail = poly(mB, pt{0.5233, 0.0948}, pt{0.5381, 0.1913}, pt{0.5959, 0.2700}, pt{0.6464, 0.3536}, pt{0.7300, 0.4041},
		pt{0.8087, 0.4619}, pt{0.9052, 0.4767}, mR)
	neck = poly(mL, pt{0.0948, 0.5233}, pt{0.1913, 0.5381}, pt{0.2700, 0.5959}, pt{0.3536, 0.6464}, pt{0.4041, 0.7300},
		pt{0.4619, 0.8087}, pt{0.4767, 0.9052}, mT)
	head = poly(pt{0, 0.8192}, pt{0.0300, 0.8492}, pt{0.0692, 0.8330}, pt{0.0854, 0.8721}, pt{0.1279, 0.8721},
		pt{0.1279, 0.9146}, pt{0.1670, 0.9308}, pt{0.1508, 0.9700}, pt{0.1808, 1})
	wing = poly(pt{0, 0.4102}, pt{0.1173, 0.4102}, pt{0.1808, 0.4365}, pt{0.2071, 0.3730}, pt{0.3730, 0.2071},
		pt{0.4365, 0.1808}, pt{0.4102, 0.1173}, pt{0.4102, 0})
)

func poly(ps ...pt) []seg {
	var out []seg
	for i := 0; i+1 < len(ps); i++ {
		out = append(out, seg{ps[i], ps[i+1]})
	}
	return out
}
