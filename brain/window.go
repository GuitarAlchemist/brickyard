package main

import (
	"strings"
	"time"
	"unicode"
)

// Delta moves, sizes and hides its own window when asked aloud, without Claude: "Delta, move to the top left",
// "maximize your window", "hide", "come back", or « Delta, mets-toi en haut à gauche », « cache-toi », « reviens ».
// Claude has the same through the window tool. A request is recognised in English and in French whatever the
// session's language, so that a French phrase in an English session still works; what Delta says back is in the
// session's language (lang.go).

// windowActions are what the scene does with its window (Placement.Actions), and what Delta says as it does it, by
// language code.
var windowActions = map[string]map[string]string{
	"maximize":   {"en": "Maximizing my window.", "fr": "J'agrandis ma fenêtre."},
	"fullscreen": {"en": "Going full screen.", "fr": "Je passe en plein écran."},
	"minimize":   {"en": "Down to the taskbar.", "fr": "Je me réduis dans la barre des tâches."},
	"restore":    {"en": "Back to my usual size.", "fr": "Je reprends ma taille."},
	"tray": {"en": "I'll wait by the clock. Say Delta, come back, to see me again.",
		"fr": "Je me range près de l'horloge. Dis Delta, reviens, pour me revoir."},
	"show":         {"en": "Here I am again.", "fr": "Me revoilà."},
	"on-top":       {"en": "I'll stay on top.", "fr": "Je reste au premier plan."},
	"not-on-top":   {"en": "I won't stay on top any more.", "fr": "Je ne reste plus au premier plan."},
	"smaller":      {"en": "Getting smaller.", "fr": "Je rapetisse."},
	"larger":       {"en": "Getting bigger.", "fr": "Je grandis."},
	"center":       {"en": "Moving to the center.", "fr": "Je me mets au centre."},
	"top-left":     {"en": "Moving to the top left.", "fr": "Je me mets en haut à gauche."},
	"top-right":    {"en": "Moving to the top right.", "fr": "Je me mets en haut à droite."},
	"bottom-left":  {"en": "Moving to the bottom left.", "fr": "Je me mets en bas à gauche."},
	"bottom-right": {"en": "Moving to the bottom right.", "fr": "Je me mets en bas à droite."},
	"left":         {"en": "Moving to the left.", "fr": "Je me mets à gauche."},
	"right":        {"en": "Moving to the right.", "fr": "Je me mets à droite."},
	"top":          {"en": "Moving to the top.", "fr": "Je me mets en haut."},
	"bottom":       {"en": "Moving to the bottom.", "fr": "Je me mets en bas."},
}

// shelfAnswers are the moves through the shelf in Delta's window (Link.ShelfMoves), and what Delta says as it makes
// them, by language code: nothing for a step, which the panel shows.
var shelfAnswers = map[string]map[string]string{
	"open":          {"en": "Here is my menu.", "fr": "Voici mon menu."},
	"sessions":      {"en": "Here are the sessions.", "fr": "Voici les sessions."},
	"conversations": {"en": "Here are the conversations.", "fr": "Voici les conversations."},
	"plugins":       {"en": "Here are the plugins.", "fr": "Voici les plugins."},
	"marketplaces":  {"en": "Here are the marketplaces.", "fr": "Voici les marketplaces."},
	"close":         {"en": "Putting my menu away.", "fr": "Je range le menu."},
	"up":            {}, "down": {}, "enter": {}, "back": {},
}

// inLanguage is what Delta says in the language with this code, the English when it has nothing in that one.
func inLanguage(answers map[string]string, code string) string {
	if a, ok := answers[code]; ok {
		return a
	}
	return answers["en"]
}

// shelfIdle is how long the shelf stays open without a move: the scene closes it then (Shelf.Tick).
const shelfIdle = 45 * time.Second

// moods are the expressions Delta's face takes (Link.Moods).
var moods = []string{"neutral", "joy", "surprise", "doubt", "sorry", "proud", "wink", "focus", "sleepy"}

// A phrase is matched on whole words of the sentence lowered, without accents and with its hyphens and apostrophes
// made spaces (plainWords). In each list the first phrase the sentence holds wins.
type phrase struct{ words, action string }

// windowWords are the requests that need nothing else: French, then English.
var windowWords = []phrase{
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

	{"full screen", "fullscreen"}, {"fullscreen", "fullscreen"},
	{"restore your window", "restore"}, {"restore yourself", "restore"}, {"normal size", "restore"}, {"usual size", "restore"},
	{"notification area", "tray"}, {"hide yourself", "tray"}, {"go hide", "tray"},
	{"come back", "show"}, {"show yourself", "show"}, {"reappear", "show"},
	{"not on top", "not-on-top"}, {"stop staying on top", "not-on-top"}, {"stop being on top", "not-on-top"},
	{"don t stay on top", "not-on-top"}, {"no longer on top", "not-on-top"},
	{"stay on top", "on-top"}, {"always on top", "on-top"}, {"on top of everything", "on-top"},
	{"yourself smaller", "smaller"}, {"your window smaller", "smaller"}, {"get smaller", "smaller"}, {"shrink yourself", "smaller"},
	{"yourself bigger", "larger"}, {"yourself larger", "larger"}, {"your window bigger", "larger"}, {"get bigger", "larger"},
}

// windowAlone are one-word requests, which count only when the sentence says nothing else: "Delta, hide." hides
// Delta, while "Delta, hide the terminal." is for Claude.
var windowAlone = map[string]string{"hide": "tray", "restore": "restore", "smaller": "smaller", "shrink": "smaller",
	"bigger": "larger", "larger": "larger", "grow": "larger"}

// windowPlaces are where Delta can go, longest first, French then English; a place needs a verb that moves Delta
// (windowMoves). An English side needs its preposition: "go ahead, all right" goes nowhere.
var windowPlaces = []phrase{
	{"en haut a gauche", "top-left"}, {"en haut a droite", "top-right"},
	{"en bas a gauche", "bottom-left"}, {"en bas a droite", "bottom-right"},
	{"coin superieur gauche", "top-left"}, {"coin superieur droit", "top-right"},
	{"coin inferieur gauche", "bottom-left"}, {"coin inferieur droit", "bottom-right"},
	{"au centre", "center"}, {"au milieu", "center"},
	{"a gauche", "left"}, {"a droite", "right"}, {"en haut", "top"}, {"en bas", "bottom"},

	{"top left", "top-left"}, {"top right", "top-right"}, {"bottom left", "bottom-left"}, {"bottom right", "bottom-right"},
	{"upper left", "top-left"}, {"upper right", "top-right"}, {"lower left", "bottom-left"}, {"lower right", "bottom-right"},
	{"the center", "center"}, {"the centre", "center"}, {"the middle", "center"},
	{"to the left", "left"}, {"on the left", "left"}, {"left side", "left"}, {"move left", "left"}, {"go left", "left"},
	{"to the right", "right"}, {"on the right", "right"}, {"right side", "right"}, {"move right", "right"}, {"go right", "right"},
	{"to the top", "top"}, {"at the top", "top"}, {"up top", "top"},
	{"to the bottom", "bottom"}, {"at the bottom", "bottom"},
}

var windowMoves = []string{"mets toi", "met toi", "place toi", "deplace toi", "range toi", "pousse toi", "colle toi",
	"installe toi", "va", "vas", "bouge", "ta fenetre", "deplace ta fenetre", "mets ta fenetre",
	"move to", "move over", "move yourself", "move your window", "move left", "move right", "go", "dock", "snap",
	"put yourself", "place yourself", "put your window"}

// windowCommand is the window action a short spoken sentence asks of Delta, or "". A long sentence is left to Claude,
// which may ask for more than the window.
func windowCommand(text string) string {
	words := plainWords(text)
	if len(words) > 10 {
		return ""
	}
	if a, ok := windowAlone[alone(words)]; ok {
		return a
	}
	line := " " + strings.Join(words, " ") + " "
	for _, w := range windowWords {
		if strings.Contains(line, " "+w.words+" ") {
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
		if strings.Contains(line, " "+p.words+" ") {
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

// loving says whether a sentence tells Delta the user loves it, in French or English: « je t'aime », "love you", and
// not « je ne t'aime pas » or "I don't love you".
func loving(text string) bool {
	line := " " + strings.Join(plainWords(text), " ") + " "
	for _, no := range []string{" ne t aime ", " t aime pas ", " ne t adore ", " t adore pas ", " don t love you ", " do not love you "} {
		if strings.Contains(line, no) {
			return false
		}
	}
	for _, yes := range []string{" je t aime ", " je t adore ", " love you ", " love ya ", " luv you "} {
		if strings.Contains(line, yes) {
			return true
		}
	}
	return false
}

// alone is what a sentence asks once Delta's name and "please" are left out: the request itself when it is one word.
func alone(words []string) string {
	var kept []string
	for _, w := range words {
		switch w {
		case "delta", "delter", "please", "stp":
		default:
			kept = append(kept, w)
		}
	}
	return strings.Join(kept, " ")
}

// shelfWords open the shelf of sessions and plugins in Delta's window, or close it, French then English; shelfMoves
// and shelfAlone move through it, and count only while it is open. Within each language a closing phrase comes before
// the phrases it holds.
var shelfWords = []phrase{
	{"ferme le menu", "close"}, {"cache le menu", "close"}, {"ferme l etagere", "close"},
	{"les sessions", "sessions"}, {"des sessions", "sessions"},
	{"les conversations", "conversations"}, {"des conversations", "conversations"}, {"mes conversations", "conversations"},
	{"nos conversations", "conversations"}, {"anciennes conversations", "conversations"},
	{"les plugins", "plugins"}, {"des plugins", "plugins"},
	{"les marketplaces", "marketplaces"}, {"des marketplaces", "marketplaces"}, {"les catalogues", "marketplaces"},
	{"ouvre le menu", "open"}, {"montre le menu", "open"}, {"affiche le menu", "open"}, {"ton menu", "open"},

	{"close the menu", "close"}, {"close your menu", "close"}, {"hide the menu", "close"}, {"hide your menu", "close"},
	{"close the shelf", "close"},
	{"the sessions", "sessions"}, {"my sessions", "sessions"}, {"your sessions", "sessions"}, {"code sessions", "sessions"},
	{"claude sessions", "sessions"},
	{"the conversations", "conversations"}, {"my conversations", "conversations"}, {"our conversations", "conversations"},
	{"past conversations", "conversations"}, {"old conversations", "conversations"},
	{"the plugins", "plugins"}, {"my plugins", "plugins"}, {"your plugins", "plugins"}, {"installed plugins", "plugins"},
	{"the marketplaces", "marketplaces"}, {"the marketplace", "marketplaces"}, {"the catalogs", "marketplaces"},
	{"the catalogues", "marketplaces"},
	{"your menu", "open"}, {"the menu", "open"},
}

// shelfShows are the verbs that open a section: "close all the sessions" and « ferme toutes les sessions » are for
// Claude.
var shelfShows = []string{"montre", "montre moi", "affiche", "ouvre", "liste", "voir", "fais voir",
	"show", "show me", "list", "open", "display", "see", "pull up", "bring up"}

var shelfMoves = []phrase{
	{"reviens en arriere", "back"}, {"retour", "back"}, {"en arriere", "back"},
	{"descends", "down"}, {"suivant", "down"}, {"en dessous", "down"},
	{"monte", "up"}, {"precedent", "up"}, {"au dessus", "up"},
	{"ouvre", "enter"}, {"entre", "enter"}, {"valide", "enter"}, {"choisis", "enter"},

	{"go back", "back"}, {"back up", "back"},
	{"go down", "down"}, {"move down", "down"}, {"scroll down", "down"}, {"next one", "down"},
	{"go up", "up"}, {"move up", "up"}, {"scroll up", "up"}, {"previous one", "up"},
	{"open it", "enter"}, {"open this", "enter"}, {"open that", "enter"}, {"select it", "enter"}, {"go in", "enter"},
}

// shelfAlone are one-word English steps: "Delta, open." opens the row, "Delta, open the README." is for Claude.
var shelfAlone = map[string]string{"back": "back", "down": "down", "next": "down", "up": "up", "previous": "up",
	"open": "enter", "enter": "enter", "select": "enter"}

// shelfCommand is the shelf move a short spoken sentence asks of Delta, or "". open says whether the shelf is open.
func shelfCommand(text string, open bool) string {
	words := plainWords(text)
	if len(words) > 10 {
		return ""
	}
	line := " " + strings.Join(words, " ") + " "
	shows := false
	for _, v := range shelfShows {
		shows = shows || strings.Contains(line, " "+v+" ")
	}
	for _, w := range shelfWords {
		if strings.Contains(line, " "+w.words+" ") && (w.action == "close" || shows) {
			return w.action
		}
	}
	if !open {
		return ""
	}
	for _, m := range shelfMoves {
		if strings.Contains(line, " "+m.words+" ") {
			return m.action
		}
	}
	return shelfAlone[alone(words)]
}
