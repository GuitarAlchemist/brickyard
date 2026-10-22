package main

import (
	"strings"
	"time"
	"unicode"
)

// Delta moves, sizes and hides its own window when asked aloud, without Claude: "Delta, mets-toi en haut à gauche",
// "maximise ta fenêtre", "cache-toi", "reviens". Claude has the same through the window tool.

// windowActions are what the scene does with its window, and what Delta says as it does it.
var windowActions = map[string]string{
	"maximize":     "J'agrandis ma fenêtre.",
	"fullscreen":   "Je passe en plein écran.",
	"minimize":     "Je me réduis dans la barre des tâches.",
	"restore":      "Je reprends ma taille.",
	"tray":         "Je me range près de l'horloge. Dis Delta, reviens, pour me revoir.",
	"show":         "Me revoilà.",
	"on-top":       "Je reste au premier plan.",
	"not-on-top":   "Je ne reste plus au premier plan.",
	"smaller":      "Je rapetisse.",
	"larger":       "Je grandis.",
	"center":       "Je me mets au centre.",
	"top-left":     "Je me mets en haut à gauche.",
	"top-right":    "Je me mets en haut à droite.",
	"bottom-left":  "Je me mets en bas à gauche.",
	"bottom-right": "Je me mets en bas à droite.",
	"left":         "Je me mets à gauche.",
	"right":        "Je me mets à droite.",
	"top":          "Je me mets en haut.",
	"bottom":       "Je me mets en bas.",
}

// shelfAnswers are the moves through the shelf in Delta's window (Link.ShelfMoves), and what Delta says as it makes
// them: nothing for a step, which the panel shows.
var shelfAnswers = map[string]string{
	"open":         "Voici mon menu.",
	"sessions":     "Voici les sessions.",
	"plugins":      "Voici les plugins.",
	"marketplaces": "Voici les marketplaces.",
	"close":        "Je range le menu.",
	"up":           "", "down": "", "enter": "", "back": "",
}

// shelfIdle is how long the shelf stays open without a move: the scene closes it then (Shelf.Tick).
const shelfIdle = 45 * time.Second

// moods are the expressions Delta's face takes (Link.Moods).
var moods = []string{"neutral", "joy", "surprise", "doubt", "sorry", "proud", "wink", "focus", "sleepy"}

// windowWords are the requests, checked in order: the first whose phrase the sentence holds wins. A phrase is matched
// on whole words of the sentence lowered, without accents and with its hyphens and apostrophes made spaces.
var windowWords = []struct{ phrase, action string }{
	{"plein ecran", "fullscreen"},
	{"maximise", "maximize"}, {"maximiser", "maximize"}, {"maximize", "maximize"}, {"agrandis toi", "maximize"}, {"agrandis ta fenetre", "maximize"},
	{"minimise", "minimize"}, {"minimiser", "minimize"}, {"minimize", "minimize"}, {"reduis toi", "minimize"}, {"reduis ta fenetre", "minimize"},
	{"restaure", "restore"}, {"restaurer", "restore"}, {"taille normale", "restore"}, {"reprends ta taille", "restore"},
	{"zone de notification", "tray"}, {"tray", "tray"}, {"cache toi", "tray"}, {"disparais", "tray"},
	{"reviens", "show"}, {"montre toi", "show"}, {"reapparais", "show"}, {"affiche toi", "show"},
	{"plus au premier plan", "not-on-top"}, {"arriere plan", "not-on-top"},
	{"premier plan", "on-top"}, {"toujours visible", "on-top"}, {"au dessus des autres", "on-top"},
	{"plus petit", "smaller"}, {"plus petite", "smaller"}, {"rapetisse", "smaller"},
	{"plus grand", "larger"}, {"plus grande", "larger"}, {"grandis", "larger"}, {"grossis", "larger"},
}

// windowPlaces are where Delta can go, longest first; a place needs a verb that moves Delta (windowMoves).
var windowPlaces = []struct{ phrase, action string }{
	{"en haut a gauche", "top-left"}, {"en haut a droite", "top-right"},
	{"en bas a gauche", "bottom-left"}, {"en bas a droite", "bottom-right"},
	{"coin superieur gauche", "top-left"}, {"coin superieur droit", "top-right"},
	{"coin inferieur gauche", "bottom-left"}, {"coin inferieur droit", "bottom-right"},
	{"au centre", "center"}, {"au milieu", "center"},
	{"a gauche", "left"}, {"a droite", "right"}, {"en haut", "top"}, {"en bas", "bottom"},
}

var windowMoves = []string{"mets toi", "met toi", "place toi", "deplace toi", "range toi", "pousse toi", "colle toi",
	"installe toi", "va", "vas", "bouge", "ta fenetre", "deplace ta fenetre", "mets ta fenetre"}

// windowCommand is the window action a short spoken sentence asks of Delta, or "". A long sentence is left to Claude,
// which may ask for more than the window.
func windowCommand(text string) string {
	words := plainWords(text)
	if len(words) > 10 {
		return ""
	}
	line := " " + strings.Join(words, " ") + " "
	for _, w := range windowWords {
		if strings.Contains(line, " "+w.phrase+" ") {
			return w.action
		}
	}
	moves := false
	for _, m := range windowMoves {
		if strings.Contains(line, " "+m+" ") {
			moves = true
			break
		}
	}
	if !moves {
		return ""
	}
	for _, p := range windowPlaces {
		if strings.Contains(line, " "+p.phrase+" ") {
			return p.action
		}
	}
	return ""
}

// plainWords are a sentence's words, lowered and without accents, its hyphens and apostrophes made spaces.
func plainWords(text string) []string {
	fold := strings.NewReplacer("à", "a", "â", "a", "ä", "a", "é", "e", "è", "e", "ê", "e", "ë", "e", "î", "i", "ï", "i",
		"ô", "o", "ö", "o", "ù", "u", "û", "u", "ü", "u", "ç", "c", "’", " ", "'", " ", "-", " ")
	clean := strings.Map(func(r rune) rune {
		if unicode.IsLetter(r) || unicode.IsDigit(r) || r == ' ' {
			return r
		}
		return ' '
	}, fold.Replace(strings.ToLower(text)))
	return strings.Fields(clean)
}

// shelfWords open the shelf of sessions and plugins in Delta's window, or close it; shelfMoves move through it, and
// count only while it is open. Both are matched as windowWords are.
var shelfWords = []struct{ phrase, action string }{
	{"ferme le menu", "close"}, {"cache le menu", "close"}, {"ferme l etagere", "close"},
	{"les sessions", "sessions"}, {"des sessions", "sessions"},
	{"les plugins", "plugins"}, {"des plugins", "plugins"},
	{"les marketplaces", "marketplaces"}, {"des marketplaces", "marketplaces"}, {"les catalogues", "marketplaces"},
	{"ouvre le menu", "open"}, {"montre le menu", "open"}, {"affiche le menu", "open"}, {"ton menu", "open"},
}

var shelfMoves = []struct{ phrase, action string }{
	{"reviens en arriere", "back"}, {"retour", "back"}, {"en arriere", "back"},
	{"descends", "down"}, {"suivant", "down"}, {"en dessous", "down"},
	{"monte", "up"}, {"precedent", "up"}, {"au dessus", "up"},
	{"ouvre", "enter"}, {"entre", "enter"}, {"valide", "enter"}, {"choisis", "enter"},
}

// shelfCommand is the shelf move a short spoken sentence asks of Delta, or "". open says whether the shelf is open.
func shelfCommand(text string, open bool) string {
	words := plainWords(text)
	if len(words) > 10 {
		return ""
	}
	line := " " + strings.Join(words, " ") + " "
	shows := false // a section opens only when asked to be shown: "ferme toutes les sessions" is for Claude
	for _, v := range []string{"montre", "montre moi", "affiche", "ouvre", "liste", "voir", "fais voir"} {
		shows = shows || strings.Contains(line, " "+v+" ")
	}
	for _, w := range shelfWords {
		if strings.Contains(line, " "+w.phrase+" ") && (w.action == "close" || shows) {
			return w.action
		}
	}
	if !open {
		return ""
	}
	for _, m := range shelfMoves {
		if strings.Contains(line, " "+m.phrase+" ") {
			return m.action
		}
	}
	return ""
}
