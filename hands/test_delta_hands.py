"""The gestures' rules, without a camera or MediaPipe: python hands\\test_delta_hands.py"""

import os
import re
import sys
import unittest

from delta_hands import AGAIN, HOLD, POSES, RULE_SURE, Tracker, pointing_down, pose

FPS = 30


def frames(tracker, seconds, start=0.0, hand="right", wrist=lambda t: (0.5, 0.5), shown="thumbs_up", sure=0.9):
    """Feeds the tracker a camera's frames, the wrist where wrist(t) puts it, and returns what it told the brain."""
    told = []
    for i in range(int(seconds * FPS)):
        t = start + i / FPS
        seen = tracker.see(t, hand, wrist(t), shown, sure)
        if seen:
            told.append((round(t, 2), seen.gesture))
    return told


def hand(index_tip, curl=True):
    """21 points of a hand whose wrist is at (0.5, 0.3), its knuckles below, and its index tip at index_tip."""
    points = [(0.5, 0.3)] + [(0.5, 0.3)] * 20
    for finger, (knuckle, joint, tip) in enumerate([(5, 6, 8), (9, 10, 12), (13, 14, 16), (17, 18, 20)]):
        x = 0.47 + 0.02 * finger
        points[knuckle], points[joint] = (x, 0.45), (x, 0.52)
        points[tip] = (x, 0.42) if curl else (x, 0.62)  # curled back toward the wrist, or stretched past the joint
    points[8] = index_tip
    return points


class Poses(unittest.TestCase):
    def test_every_gesture_sent_is_one_the_brain_knows(self):
        with open(os.path.join(os.path.dirname(__file__), "..", "brain", "gesture.go"), encoding="utf-8") as f:
            go = f.read()
        table = re.search(r"var gestures = map\[string\]struct \{.*?\n\}\{\n(.*?)\n\}", go, re.S).group(1)
        known = set(re.findall(r'^\t"(\w+)":', table, re.M))
        self.assertIn("thumbs_up", known)
        sent = set(POSES.values()) | {"point_down", "swipe_left", "swipe_right", "wave"}
        self.assertFalse(sent - known, "the brain answers 400 to these")

    def test_the_models_poses_take_the_brains_names(self):
        self.assertEqual(pose("ILoveYou", 0.93, None), ("i_love_you", 0.93))
        self.assertEqual(pose("Victory", 0.9, None), (None, 0.0))
        self.assertEqual(pose(None, 0.0, None), (None, 0.0))

    def test_pointing_down_is_found_by_the_hand_itself(self):
        down = hand(index_tip=(0.47, 0.7))
        self.assertTrue(pointing_down(down))
        self.assertEqual(pose("Pointing_Up", 0.7, down), ("point_down", RULE_SURE))
        self.assertFalse(pointing_down(hand(index_tip=(0.47, 0.42))), "the index curled as the others")
        self.assertFalse(pointing_down(hand(index_tip=(0.47, 0.7), curl=False)), "an open hand")
        self.assertFalse(pointing_down(hand(index_tip=(0.75, 0.55))), "pointing sideways")


class Held(unittest.TestCase):
    def test_a_pose_held_still_is_told_once(self):
        tracker = Tracker()
        told = frames(tracker, 2.0)
        self.assertEqual([g for _, g in told], ["thumbs_up"])
        self.assertGreaterEqual(told[0][0], HOLD)
        self.assertLess(told[0][0], HOLD + 0.1)

    def test_shown_again_it_waits_for_the_pause(self):
        tracker = Tracker()
        frames(tracker, 0.5)
        frames(tracker, 0.2, start=0.5, shown=None)
        self.assertEqual(frames(tracker, 0.5, start=0.7), [], "shown again too soon")
        frames(tracker, 0.2, start=1.2, shown=None)
        self.assertEqual([g for _, g in frames(tracker, 0.6, start=AGAIN + 0.1)], ["thumbs_up"])

    def test_a_pose_on_the_move_is_not_told(self):
        tracker = Tracker()
        self.assertEqual(frames(tracker, 1.0, wrist=lambda t: (0.3 + 0.4 * t, 0.5)), [])

    def test_the_hand_leaving_ends_the_hold(self):
        tracker = Tracker()
        frames(tracker, 0.2)
        tracker.see(0.2, wrist=None)
        told = frames(tracker, 0.5, start=0.25)
        self.assertEqual(len(told), 1)
        self.assertGreaterEqual(told[0][0], 0.25 + HOLD)


class Moves(unittest.TestCase):
    def test_swipes_either_way(self):
        right = frames(Tracker(), 0.6, wrist=lambda t: (0.2 + t, 0.5), shown=None)
        left = frames(Tracker(), 0.6, wrist=lambda t: (0.8 - t, 0.5), shown=None)
        self.assertEqual([g for _, g in right], ["swipe_right"])
        self.assertEqual([g for _, g in left], ["swipe_left"])

    def test_a_slow_or_steep_move_is_no_swipe(self):
        self.assertEqual(frames(Tracker(), 1.0, wrist=lambda t: (0.3 + 0.2 * t, 0.5), shown=None), [])
        self.assertEqual(frames(Tracker(), 0.6, wrist=lambda t: (0.2 + t, 0.2 + t), shown=None), [])

    def test_an_open_hand_waving(self):
        import math

        wave = lambda t: (0.5 + 0.06 * math.sin(2 * math.pi * 2.5 * t), 0.5)
        told = frames(Tracker(), 1.2, wrist=wave, shown="open_palm")
        self.assertEqual([g for _, g in told], ["wave"], "and no open palm meanwhile")
        self.assertEqual(frames(Tracker(), 1.2, wrist=wave, shown="fist"), [], "a fist shaken is no wave")


if __name__ == "__main__":
    # On stdout: Windows PowerShell, which CI runs check.ps1 with, can take a native command's stderr for a failure.
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout, verbosity=1))
