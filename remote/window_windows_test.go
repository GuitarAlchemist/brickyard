package main

import "testing"

// While Delta's window is closed the capture looks for it again and again: on 2026-10-31 the gateway died after
// about 2000 looks, "fatal error: too many callback functions", since each one made a new Windows callback.
func TestLookingForAClosedWindowAgainAndAgainDoesNotKillTheGateway(t *testing.T) {
	for i := 0; i < 3000; i++ {
		if _, err := findWindow("no Delta window has this title", "scene.exe"); err == nil {
			t.Fatal("found a window nobody opened")
		}
	}
}
