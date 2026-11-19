# /// script
# requires-python = ">=3.10"
# dependencies = [
#     "mediapipe==1.1.1",                 # Apache-2.0: the hand's landmarks and its canned poses
#     "opencv-contrib-python==5.0.0.93",  # Apache-2.0: the camera; mediapipe requires it already
# ]
# ///
"""Delta's eyes for a few hand gestures.

Start it yourself, it turns the camera on, and Ctrl+C turns it off:

    uv run hands\\delta_hands.py [--camera N] [--preview] [--brain URL]

It reads the camera with MediaPipe's gesture recognizer and posts what it understands to the brain's POST /gesture
(brain/gesture.go): a pose held still for a moment (thumb up or down, open palm, fist, pointing up or down, the « je
t'aime » sign), a swipe left or right, a wave. Frames stay in memory: only the gesture's name, the hand, how sure it is
and when go to 127.0.0.1. The model, 8 MB, is downloaded once to %LOCALAPPDATA%\\delta\\models and checked by SHA-256.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import sys
import time
import urllib.error
import urllib.request
from collections import deque
from dataclasses import dataclass

# MediaPipe's gesture recognizer, version 1 in float16: a hand landmarker, then a classifier of seven poses. MediaPipe's
# code is Apache-2.0; the model's card names no licence (THIRD_PARTY.md).
MODEL_URL = "https://storage.googleapis.com/mediapipe-models/gesture_recognizer/gesture_recognizer/float16/1/gesture_recognizer.task"
MODEL_SHA256 = "97952348cf6a6a4915c2ea1496b4b37ebabc50cbbf80571435643c455f2b0482"

# The canned poses Delta knows, under the brain's names. Victory has no use yet; "None" is no pose.
POSES = {
    "Thumb_Up": "thumbs_up",
    "Thumb_Down": "thumbs_down",
    "Open_Palm": "open_palm",
    "Closed_Fist": "fist",
    "Pointing_Up": "point_up",
    "ILoveYou": "i_love_you",
}

# Times are in seconds, distances in frame widths.
HOLD = 0.3  # how long a pose must hold, the hand still, before it counts
STILL = 0.05  # how far the wrist may drift meanwhile
AGAIN = 1.5  # the least time between two sends of the same gesture; a pose must also be dropped and shown anew
SWIPE = 0.3  # how far the wrist travels sideways, within SWIPE_TIME, for a swipe
SWIPE_TIME = 0.5
WAVE_TIME = 1.2  # a wave is an open hand going back and forth: WAVE_LEGS legs of at least WAVE_LEG within this time
WAVE_LEG = 0.04
WAVE_LEGS = 3
RULE_SURE = 0.8  # how sure a gesture these rules find is said to be: the model's own scores are for its poses

WRIST = 0
FINGERS = {"index": (5, 6, 8), "middle": (9, 10, 12), "ring": (13, 14, 16), "pinky": (17, 18, 20)}  # knuckle, middle joint, tip


def _far(a, b) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def extended(points, finger: str) -> bool:
    """A stretched finger's tip lies well beyond its middle joint, seen from the wrist."""
    _, joint, tip = FINGERS[finger]
    return _far(points[WRIST], points[tip]) > 1.1 * _far(points[WRIST], points[joint])


def curled(points, finger: str) -> bool:
    """A curled finger's tip comes back nearer the wrist than its middle joint."""
    _, joint, tip = FINGERS[finger]
    return _far(points[WRIST], points[tip]) < _far(points[WRIST], points[joint])


def pointing_down(points) -> bool:
    """The index alone stretched, toward the floor: the model knows only pointing up. The image's y grows downward."""
    if not extended(points, "index") or not all(curled(points, f) for f in ("middle", "ring", "pinky")):
        return False
    knuckle, _, tip = FINGERS["index"]
    dx, dy = points[tip][0] - points[knuckle][0], points[tip][1] - points[knuckle][1]
    return dy > 1.5 * abs(dx)


def pose(label: str | None, score: float, points) -> tuple[str | None, float]:
    """The brain's name for what the hand shows, and how sure it is, or None."""
    if points is not None and pointing_down(points):
        return "point_down", RULE_SURE
    name = POSES.get(label or "")
    return (name, score) if name else (None, 0.0)


@dataclass
class Seen:
    gesture: str
    hand: str
    confidence: float


class Tracker:
    """Turns what the model sees, frame by frame, into the few gestures worth telling the brain: a pose held still,
    a swipe, a wave. Each is told once."""

    def __init__(self) -> None:
        self.trail: deque = deque()  # (t, x, y, pose) of the wrist over the last WAVE_TIME
        self.pose: str | None = None  # the pose shown now, since self.since
        self.since = 0.0
        self.told = False  # the pose shown now was told
        self.sent: dict[str, float] = {}  # when each gesture was last sent
        self.settle = 0.0  # no pose counts before then: after a swipe or a wave, the hand is still moving

    def see(self, t: float, hand: str = "", wrist=None, shown: str | None = None, sure: float = 0.0) -> Seen | None:
        """One frame at time t: the hand, its wrist (x, y), or None for no hand, and the pose it shows, if any."""
        if wrist is None:  # the hand left: whatever it was doing is over
            self.trail.clear()
            self.pose, self.told = None, False
            return None
        self.trail.append((t, wrist[0], wrist[1], shown))
        while t - self.trail[0][0] > WAVE_TIME:
            self.trail.popleft()

        moved = self._swipe(t) or self._wave()
        if moved:
            self.trail.clear()
            self.settle = t + HOLD
            self.pose, self.told = None, False
            return self._send(t, Seen(moved, hand, RULE_SURE))

        if shown != self.pose:
            self.pose, self.since, self.told = shown, t, False
            return None
        if shown is None or self.told or t < self.settle:
            return None
        if self._drift(self.since) > STILL:  # held, but moving: the hold starts again once the hand is still
            self.since = t
            return None
        if t - self.since < HOLD:
            return None
        self.told = True
        return self._send(t, Seen(shown, hand, sure))

    def _send(self, t: float, seen: Seen) -> Seen | None:
        if t - self.sent.get(seen.gesture, -math.inf) < AGAIN:
            return None
        self.sent[seen.gesture] = t
        return seen

    def _drift(self, since: float) -> float:
        held = [(x, y) for at, x, y, _ in self.trail if at >= since]
        return max((_far(held[0], p) for p in held), default=0.0)

    def _swipe(self, t: float) -> str | None:
        recent = [(x, y) for at, x, y, _ in self.trail if t - at <= SWIPE_TIME]
        if len(recent) < 2:
            return None
        dx, dy = recent[-1][0] - recent[0][0], recent[-1][1] - recent[0][1]
        if abs(dx) < SWIPE or abs(dy) > 0.5 * abs(dx):
            return None
        return "swipe_right" if dx > 0 else "swipe_left"  # the frame is mirrored: right is the user's right

    def _wave(self) -> str | None:
        if 2 * sum(1 for *_, shown in self.trail if shown == "open_palm") < len(self.trail):
            return None
        xs = [x for _, x, _, _ in self.trail]
        way, turn, peak, legs = 0, xs[0], xs[0], 0
        for x in xs[1:]:
            if way == 0:
                if abs(x - turn) >= WAVE_LEG:
                    way, peak, legs = (1 if x > turn else -1), x, 1
            elif (x - peak) * way > 0:  # further the same way
                peak = x
            elif abs(x - peak) >= WAVE_LEG:  # back far enough: a new leg
                way, turn, peak, legs = -way, peak, x, legs + 1
        return "wave" if legs >= WAVE_LEGS else None


def read(result):
    """The first hand in a recognizer's result: which hand, its 21 points (x, y), and the model's best pose."""
    if not result.hand_landmarks:
        return "", None, None, 0.0
    points = [(p.x, p.y) for p in result.hand_landmarks[0]]
    hand = result.handedness[0][0].category_name.lower() if result.handedness and result.handedness[0] else ""
    best = result.gestures[0][0] if result.gestures and result.gestures[0] else None
    return hand, points, (best.category_name if best else None), (best.score if best else 0.0)


# No proxy, whatever the environment says: the brain is on this computer.
_local = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def post(url: str, seen: Seen) -> str:
    """Posts one gesture as brain/gesture.go reads it, and returns the brain's answer: 204, or why not."""
    body = {"gesture": seen.gesture, "hand": seen.hand, "confidence": round(seen.confidence, 3), "t_ms": int(time.time() * 1000)}
    request = urllib.request.Request(url, data=json.dumps(body).encode(), method="POST",
                                     headers={"Content-Type": "application/json", "X-Delta-Hook": "1"})
    try:
        with _local.open(request, timeout=1) as answer:
            return str(answer.status)
    except urllib.error.HTTPError as e:
        return f"{e.code} {e.read().decode(errors='replace').strip()}"
    except (urllib.error.URLError, OSError) as e:
        return f"no brain at {url}: {e}"


def _sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def model() -> str:
    """The gesture model, downloaded once and checked."""
    folder = os.path.join(os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"), "delta", "models")
    path = os.path.join(folder, "gesture_recognizer.task")
    if os.path.exists(path) and _sha256(path) == MODEL_SHA256:
        return path
    os.makedirs(folder, exist_ok=True)
    print(f"HANDS downloading MediaPipe's gesture model, 8 MB, once: {path}", flush=True)
    part = path + ".part"
    with urllib.request.urlopen(MODEL_URL, timeout=60) as answer, open(part, "wb") as f:
        shutil.copyfileobj(answer, f)
    got = _sha256(part)
    if got != MODEL_SHA256:
        os.remove(part)
        sys.exit(f"HANDS FAIL the model's SHA-256 is {got}, not {MODEL_SHA256}")
    os.replace(part, path)
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description="Delta's eyes for a few hand gestures.")
    parser.add_argument("--camera", type=int, default=0, help="which camera, 0 for the first")
    parser.add_argument("--preview", action="store_true", help="show what the camera sees, and the gestures (q or Esc closes)")
    parser.add_argument("--brain", default="http://127.0.0.1:8807/gesture", help="the brain's POST /gesture")
    args = parser.parse_args()

    import cv2  # here, not above: the tests run without the camera's packages
    import mediapipe as mp
    from mediapipe.tasks import python as tasks
    from mediapipe.tasks.python import vision

    options = vision.GestureRecognizerOptions(base_options=tasks.BaseOptions(model_asset_path=model()),
                                              running_mode=vision.RunningMode.VIDEO, num_hands=1)
    camera = cv2.VideoCapture(args.camera, cv2.CAP_DSHOW)
    if not camera.isOpened():
        print(f"HANDS FAIL camera {args.camera} does not open: another program may hold it", flush=True)
        return 1
    camera.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    camera.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    print(f"HANDS camera {args.camera} on; Ctrl+C turns it off", flush=True)
    tracker, start, last_ms, said = Tracker(), time.monotonic(), -1, ""
    try:
        with vision.GestureRecognizer.create_from_options(options) as recognizer:
            while True:
                ok, frame = camera.read()
                if not ok:
                    print(f"HANDS FAIL camera {args.camera} stopped giving frames", flush=True)
                    return 1
                frame = cv2.flip(frame, 1)  # as in a mirror, which is what MediaPipe's handedness expects
                now = time.monotonic()
                ms = max(int((now - start) * 1000), last_ms + 1)  # the recognizer wants rising timestamps
                last_ms = ms
                image = mp.Image(image_format=mp.ImageFormat.SRGB, data=cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
                hand, points, label, score = read(recognizer.recognize_for_video(image, ms))
                shown, sure = pose(label, score, points)
                seen = tracker.see(now, hand, points[WRIST] if points else None, shown, sure)
                if seen:
                    said = f"{seen.gesture} {seen.hand} {seen.confidence:.2f}"
                    print(f"HANDS {said} -> {post(args.brain, seen)}", flush=True)
                if args.preview:
                    cv2.putText(frame, f"{shown or '-'}   {said}", (12, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (80, 255, 120), 2)
                    cv2.imshow("Delta hands", frame)
                    if cv2.waitKey(1) & 0xFF in (27, ord("q")):
                        return 0
    except KeyboardInterrupt:
        return 0
    finally:
        camera.release()
        if args.preview:
            cv2.destroyAllWindows()
        print("HANDS camera off", flush=True)


if __name__ == "__main__":
    sys.exit(main())
