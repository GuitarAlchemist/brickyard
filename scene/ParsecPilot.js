// PARSEC's pilot: each animation frame it reads the game's state (ship, enemies, missiles, ground, tunnel ceiling,
// laser heat, fuel) and plays by keys alone, as a player at the cabinet would. It starts the game from its title, plans
// where to fly so that its laser lines up with enemies on the frames it does damage while keeping clear of craft, shots
// and ground, fires while something is in line and the laser is cool enough, times its refuelling, and starts again
// when the game is over.
(() => {
  const ahead = 48;            // frames foreseen when choosing a height
  const far = 72;              // frames foreseen when choosing when to chase a saucer
  const home = 14;             // the ship rests to the left, where it sees most of the screen
  const saucerFuel = 16;       // fuel the saucers' wave may take
  const calledSoon = 1.5;      // fuel left over the call's 20 when a spared enemy is let go (136 frames)
  const byniteFuel = 8;        // fuel the bynites' wave may take
  const held = {};
  let last = "", since = 0, focus = null, hot = false, lastWay = { col: home, from: -1, at: 0 };

  const press = (code, key, down) => {
    if (!!held[code] !== down) { held[code] = down; __deltaKey(down ? "keydown" : "keyup", code, key); }
  };
  const tap = (code, key) => { __deltaKey("keydown", code, key); __deltaKey("keyup", code, key); };
  const releaseAll = () => { for (const code of Object.keys(held)) press(code, code === "Space" ? " " : code, false); };

  // An enemy's box, its size read from the game's pixel art.
  const sizes = new Map();
  function box(e) {
    const key = e.kind === "asteroid" ? "asteroid" + e.variant : e.kind;
    let size = sizes.get(key);
    if (!size) {
      const rows = e.kind === "asteroid" ? SPR.asteroids[e.variant] : SPR[ENEMY_DEF[e.kind].spr];
      size = { w: Math.max(...rows.map(r => r.length)), h: rows.length };
      sizes.set(key, size);
    }
    return { x: e.x, y: e.y, w: size.w, h: size.h, back: e.dir < 0 };
  }

  // Ways to steer across: to a column, braking before it, then from a given frame on (if any) right at full speed, to
  // keep up with a saucer flying past. Saucers come back from the left at a height no one knows, so while they fly the
  // ship rests further right, to see them in time, with room left ahead to chase them; it may shift left or right of
  // that so a saucer passes it on a frame the laser does damage. Where the ship will be over the next frames is
  // foreseen for each way, as the game moves it.
  const column = () => game.spawnKind === "saucer" ? 60 : home;
  const steer = (way, x, vx, t) => {
    const want = way.from >= 0 && t >= way.from ? 1.6 : Math.max(-1.6, Math.min(1.6, (way.col - x) * 0.08));
    return vx < want - 0.12 ? 1 : vx > want + 0.12 ? -1 : 0;
  };
  function drift(way, frames) {
    const xs = [player.x];
    for (let t = 1, x = player.x, vx = player.vx; t <= frames; t++) {
      const d = steer(way, x, vx, t - 1);
      vx = d > 0 ? Math.min(1.6, vx + 0.09) : d < 0 ? Math.max(-1.6, vx - 0.09) : vx * 0.92;
      x = Math.max(4, Math.min(120, x + vx));
      xs.push(x);
    }
    return xs;
  }

  // Where an enemy will be after each of the next frames, as the game moves it (index 0 is now); short if it leaves.
  // A saucer turns back 34 pixels ahead of the ship; one that leaves by the left comes back at once, at a height no
  // one knows: when, and how fast, is kept.
  function course(e, xs) {
    const b = box(e), out = [b], acc = 0.004 + game.level * 0.0004;
    let { x, y, spd, dir } = e, turned = e.reversed;
    for (let t = 1; t < xs.length; t++) {
      if (e.kind === "swooper") { spd = Math.min(4.6, spd + acc); if (y < e.targetY) y = Math.min(e.targetY, y + 0.9); x -= spd; }
      else if (e.kind === "ltf") { spd = Math.min(5.6, spd + acc * 1.5); x -= spd; }
      else if (e.kind === "satellite") { x -= spd; y += Math.sign(player.y - y) * Math.min(Math.abs(player.y - y), 0.45); }
      else if (e.kind === "saucer") {
        x += dir * spd;
        if (dir === 1 && x > xs[t] + 34 && !turned) { dir = -1; spd += 1; turned = true; }
        if (dir === -1 && x < -24) { out.exit = t; out.speed = spd; break; }
      }
      else if (e.kind !== "asteroid" && x > e.patrolX) x -= 1.6;   // cruisers stop at their patrol line
      if (x < -30) break;
      out.push({ x, y, w: b.w, h: b.h, back: dir < 0 });
    }
    return out;
  }

  // What a hit on an enemy is worth: a cruiser or a saucer most, an asteroid least, the nearer the more, and the one
  // already aimed at a little more, so the ship does not swap targets.
  function worth(e, x) {
    const cruiser = e.kind === "urbite" || e.kind === "dramite" || e.kind === "bynite";
    return (e.kind === "asteroid" ? 100 : cruiser || e.kind === "saucer" ? 500 : 400) + (W - x) * 0.6 + (e === focus ? 80 : 0);
  }
  // Whether the laser, fired at frame t from the column xs[t], strikes the box b (where the enemy was the frame before).
  const strikes = (e, b, xs, t) => (game.frame + t) % 12 === 0 && b.x > xs[t] + 15.5 && (b.x < W - 6 || e.kind === "asteroid");

  // The ground's top at each screen column: the tunnel's flat floor while the ship is under its ceiling, else the
  // landscape, also the one that comes back once the tunnel is passed. The ceiling's bottom, or -1 where there is none.
  // Then, worked out when first asked for, the highest ground and lowest ceiling under a ship whose left edge is at a
  // column.
  const floors = [], roofs = [], floorUnder = [], roofOver = [], asked = [];
  let surveyed = 0;
  function survey() {
    const t = terrain.tunnel, inside = t && game.tunnelX + player.x < t.w;
    const strip = terrain.strips[0].h.length, all = strip * terrain.strips.length, wx = Math.floor(game.scrollX || 0);
    for (let sx = 0; sx < W; sx++) {
      const at = (((wx + sx) % all) + all) % all, in_ = Math.floor(game.tunnelX || 0) + sx;
      floors[sx] = inside ? FLOOR_Y - 2 : terrain.strips[Math.floor(at / strip)].h[at % strip];
      roofs[sx] = t && in_ >= 0 && in_ < t.w ? t.ceil[in_] : -1;
    }
    surveyed++;
  }
  function under(sx) {
    if (asked[sx] !== surveyed) {
      let f = 999, r = -1;
      for (let i = sx; i <= sx + 18 && i < W; i++) { if (floors[i] < f) f = floors[i]; if (roofs[i] > r) r = roofs[i]; }
      floorUnder[sx] = f; roofOver[sx] = r; asked[sx] = surveyed;
    }
    return sx;
  }

  // How much one way across could bring against saucers, taking no account yet of what is in the way: the shots that
  // can land on one of them while it is ahead of the ship, the ship having had the time to climb or dive to it, and,
  // on one coming back after the turn, still the time to slip away before it is on the ship. A saucer coming in from
  // the left, back or new, counts at half, its height unknown till then.
  function chase(way, spare) {
    const xs = drift(way, far), y = player.y;
    let best = { score: 0, target: null }, coming = 0;
    const entering = (from, spd, hp) => {
      let gain = 0, shots = 0;
      for (let t = from + 1, x = -20; t <= far && shots < hp; t++) {
        if ((game.frame + t) % 12 === 0 && x > xs[t] + 15.5) { gain += 250 - 4 * t; shots++; }
        x += spd;
        if (x > xs[t] + 34) break;
      }
      return gain;
    };
    for (const e of enemies) {
      if (e.dead || e === spare) continue;
      const path = course(e, xs);
      let gain = 0, shots = 0;
      for (let t = 1; t <= path.length && shots < e.hp; t++) {
        const b = path[t - 1], next = path[t + 1];
        if (!strikes(e, b, xs, t) || b.back && !(next && next.x > xs[t + 1] + 17)) continue;
        const low = b.y - 6, high = b.y + b.h - 4, gap = y < low ? low - y : y > high ? y - high : 0;
        if (gap > 3 * t) continue;
        gain += worth(e, path[0].x) - 4 * t;
        shots++;
      }
      if (gain > best.score) best = { score: gain, target: e };
      if (path.exit) coming = Math.max(coming, entering(path.exit, path.speed, e.hp));
    }
    const saucers = enemies.filter(e => !e.dead && e.kind === "saucer").length;
    if (game.waveLeft > 0 && saucers < 2) coming = Math.max(coming, entering(Math.max(0, Math.ceil(game.spawnCd)), 2.2 + 0.1 * game.level, 3));
    best.score += coming - Math.abs(xs[ahead] - column()) * 0.5;
    return best;
  }

  // The best height to make for when steering one way: get there, stay for the shots, then slip out of whatever comes.
  // A crash the ship could not slip out of in time weighs most, the shallowest first when none can be escaped; one it
  // could is only a mild nudge away. Each shot that lands before the target dies counts, sooner ones more. Without a
  // shot to take, the ship keeps as near as is safe to the height of the target it is after.
  function plan(way, spare, after, twice) {
    const { y } = player, xs = drift(way, ahead), x0 = Math.floor(Math.min(...xs)), x1 = Math.max(...xs);
    let floor = 999, roof = -1;
    for (let sx = Math.max(0, x0); sx <= x1 + 18 && sx < W; sx++) { floor = Math.min(floor, floors[sx]); roof = Math.max(roof, roofs[sx]); }
    const low = Math.max(3, roof + 2), high = Math.min(152, floor - 12);

    // Heights the ship must not be at, frame by frame (flat lists of frame, top, bottom): the ground and the ceiling
    // under and over it as they scroll by, and craft; and heights that line its gun up with a target on the frames the
    // laser does damage (one in twelve).
    const ground = [], ceiling = [];
    for (let t = 1; t <= ahead; t++) {
      const at = under(Math.min(W - 1, Math.floor(xs[t] + game.scrollSpd * t)));
      ground.push(t, floorUnder[at] - 12, 999);
      if (roofOver[at] >= 0) ceiling.push(t, -999, roofOver[at] + 2);
    }
    const dangers = [ground, ceiling], targets = [];
    for (const e of enemies) {
      if (e.dead) continue;
      const path = course(e, xs), spans = [];
      for (let t = 1; t < path.length; t++) {
        const b = path[t];
        if (b.x < xs[t] + 16 && b.x + b.w > xs[t] + 1) spans.push(t, b.y - 9, b.y + b.h - 1);
      }
      if (spans.length) dangers.push(spans);
      // A saucer back from the left, at a height drawn at random, so fast that it would be on the ship before the ship
      // could slip out of its way: from then on, as it only gets faster, the ship is to be above them all (24 to 130).
      if (path.exit) {
        const reach = Math.ceil((xs[path.exit] + 9) / path.speed);
        if (reach <= 3) {
          const refuge = [];
          for (let t = path.exit + reach; t <= ahead; t++) refuge.push(t, 10, 999);
          refuge.odds = 0.2;   // it may well miss the ship
          if (refuge.length) dangers.push(refuge);
        }
      }
      if (e === spare) continue;
      const aims = [];
      for (let t = 1; t <= path.length; t++) {
        const b = path[t - 1];
        if (strikes(e, b, xs, t)) aims.push(t, b.y - 6, b.y + b.h - 4);
      }
      if (aims.length) targets.push({ e, value: worth(e, path[0].x), aims, hp: e.hp });
    }
    for (const m of missiles) {
      const spans = [];
      for (let t = 1; t <= ahead; t++) {
        const mx = m.x + m.vx * t, my = m.y + m.vy * t;
        if (mx > xs[t] && mx < xs[t] + 17) spans.push(t, my - 10, my);
      }
      if (spans.length) dangers.push(spans);
    }

    // Each frame's dangers merged together, with those of the few frames after it and with the ground and the
    // ceiling, so that the way out of one is not taken to lead into another.
    const raw = [], merged = [];
    for (const spans of dangers) {
      spans.top = 999; spans.bottom = -999;   // the heights it spans at all, to pass over it quickly when out of reach
      for (let i = 0; i < spans.length; i += 3) { spans.top = Math.min(spans.top, spans[i + 1]); spans.bottom = Math.max(spans.bottom, spans[i + 2]); }
    }
    for (let t = 1; t <= ahead; t++) raw[t] = [];
    for (const spans of dangers) if (!spans.odds) for (let i = 0; i < spans.length; i += 3) raw[spans[i]].push(spans[i + 1], spans[i + 2]);
    for (let t = 1; t <= ahead; t++) {
      const l = [-999, low, high, 999], out = [];
      for (let u = t; u <= Math.min(ahead, t + 4); u++) for (const v of raw[u]) l.push(v);
      for (let n = l.length / 2; n > 0; n--) {   // the topmost band left goes next, joined to the last if they overlap
        let k = 0;
        for (let i = 2; i < l.length; i += 2) if (l[i] < l[k]) k = i;
        const a = l[k], b = l[k + 1];
        l[k] = 1e9;
        if (out.length && a < out[out.length - 1]) out[out.length - 1] = Math.max(out[out.length - 1], b);
        else out.push(a, b);
      }
      merged[t] = out;
    }
    const deep = (t, sy) => {
      const l = merged[t];
      for (let i = 0; i < l.length; i += 2) if (sy > l[i] && sy < l[i + 1]) return Math.min(sy - l[i], l[i + 1] - sy);
      return 0;
    };

    const lean = -Math.abs(xs[ahead] - column()) * 0.5;   // back to the column when nothing is gained away from it
    const pull = after && !after.dead ? box(after) : null;
    const hits = [];
    let best = { score: -Infinity, c: y, target: null };
    // When the keys pressed now may hold over two frames, the ship that stays put cannot slip out before the second
    // frame, and a single step may overshoot: into danger, it is then the last resort.
    const least = twice ? 2 : 0;
    const overshoot = sy => {
      if (sy < low || sy > high) return 1e5;
      for (let i = 0; i < raw[2].length; i += 2) if (sy > raw[2][i] && sy < raw[2][i + 1]) return 7e4;
      return 0;
    };
    for (let k = -50; k <= 50; k++) {
      const c = y + 3 * k, arrive = Math.abs(k);
      if (c < 3 || c > 152) continue;
      // The first frame each danger would catch the ship on its way to c, how deep in, and so the last frame the ship
      // may stay before it has to slip out; then what that costs when the ship stays until a given frame.
      hits.length = 0;
      for (const spans of dangers) {
        if (Math.max(y, c) <= spans.top || Math.min(y, c) >= spans.bottom) continue;
        for (let i = 0; i < spans.length; i += 3) {
          const t = spans[i], sy = y + Math.max(-3 * t, Math.min(3 * t, c - y));
          if (sy > spans[i + 1] && sy < spans[i + 2]) {
            const odds = spans.odds || 1, depth = odds < 1 ? Math.min(sy - spans[i + 1], spans[i + 2] - sy) : deep(t, sy);
            hits.push(t - Math.ceil(depth / 3), odds * (1e5 + 1000 * depth), odds * 1500 / t);
            break;
          }
        }
      }
      const risk = stay => {
        let cost = 0;
        for (let i = 0; i < hits.length; i += 3) cost += hits[i] < stay ? hits[i + 1] : hits[i + 2];
        return cost;
      };
      let base = lean - arrive * 2 - Math.abs(c - 70) * 0.3;
      if (pull) base -= Math.abs(c - (pull.y + pull.h / 2 - 5)) * 0.5;
      if (c < low) base -= 1e6 + (low - c) * 1e3;
      if (c > high) base -= 1e6 + (c - high) * 1e3;
      if (twice && arrive === 1) base -= overshoot(c + c - y);
      let score = base - risk(Math.max(arrive, least));
      if (score > best.score) best = { score, c, target: null };
      for (const target of targets) {
        const aims = target.aims;
        let gain = 0, shots = 0;
        for (let i = 0; i < aims.length && shots < target.hp; i += 3) {
          const t = aims[i], sy = y + Math.max(-3 * t, Math.min(3 * t, c - y));   // on the way there, or there
          if (sy < aims[i + 1] || sy > aims[i + 2]) continue;
          gain += target.value - 4 * t;
          shots++;
          score = base + gain - risk(Math.max(t, arrive, least));
          if (score > best.score) best = { score, c, target: target.e };
        }
      }
    }
    return best;
  }

  function fly() {
    // The game calls for the tunnel once the fuel is down to 20, and the tunnel comes when the sky is clear, the rest
    // of the wave skipped; it takes fuel to fly through. A call during the bynites' wave, though, is followed by the
    // asteroid belt, where there is no tunnel, and after the belt the next level's tunnel is called for at once. So
    // when the fuel left after the saucers would not see the ship through the bynites, the belt and that tunnel, the
    // ship spares the last enemy of the wave before the saucers until the call is near: it comes then before the
    // saucers' wave, or early in it, with one or two of them out. Later, the tunnel would wait for the last of them,
    // ever faster, while the fuel it takes runs out.
    const kind = game.spawnKind, alive = enemies.filter(e => !e.dead && e.kind === kind);
    const level = game.level + 1, length = 420 + 240 * Math.min(2, game.tunnelIndex) + 30 * level + 2 * W;
    const tunnel = (length / (1 + 0.08 * (level - 1)) + 150) * 0.011;
    const need = saucerFuel + byniteFuel + ((15 + 3 * game.level) * 60 + 240) * 0.011 + tunnel + 2;
    const spare = game.phase === "waves" && game.waveIdx === 3 && !game.refuelCalled && game.waveLeft === 0 &&
      alive.length === 1 && game.fuel > 20 + calledSoon && game.fuel < need ? alive[0] : null;
    survey();

    // The game moves on by fixed steps of a sixtieth of a second, as many as the time since its last animation frame
    // holds, so at a real sixty frames a second it now and then runs two, or none: two may come next when the time it
    // has left over is past half a step (but not a hair short of a whole one, as frames that come exactly on time
    // leave it, by rounding).
    const twice = typeof acc === "number" && acc > 1000 / 120 && acc < 1000 / 60 - 1e-6;

    // Against saucers, the way across is the one that could bring the most shots, then the height is planned along
    // it; against the others, each way is planned in full. The way kept a frame ago wins ties, so the ship does not
    // dither.
    const was = lastWay.from < 0 ? -1 : Math.max(0, lastWay.from - (game.frame - lastWay.at));
    const kept = w => w.col === lastWay.col && (w.from < 0 ? was < 0 : was >= 0 && Math.abs(w.from - was) <= 2);
    let way = null, pick = null;
    if (kind === "saucer" && enemies.length) {
      let after = null, top = -Infinity;
      for (const col of [40, 60, 80]) {
        for (const from of [-1, 0, 4, 8, 12, 16, 20, 26, 32, 40, 50]) {
          const w = { col, from }, c = chase(w, spare), score = c.score + (kept(w) ? 10 : 0);
          if (score > top) { top = score; way = w; after = c.target; }
        }
      }
      if (!after) for (const e of enemies) if (!e.dead && e.dir > 0 && !e.reversed && e.x < player.x + 15 && (!after || e.x > after.x)) after = e;
      pick = plan(way, spare, after, twice);
      if (!pick.target) pick.target = after;
    } else {
      for (const w of enemies.length ? [{ col: column(), from: -1 }, { col: column(), from: 0 }] : [{ col: column(), from: -1 }]) {
        const p = plan(w, spare, null, twice);
        if (kept(w)) p.score += 10;
        if (!pick || p.score > pick.score) { pick = p; way = w; }
      }
    }
    lastWay = { col: way.col, from: way.from, at: game.frame };
    focus = pick.target;
    const { x, y } = player;
    press("ArrowUp", "ArrowUp", pick.c < y);
    press("ArrowDown", "ArrowDown", pick.c > y);
    const across = steer(way, x, player.vx, 0);
    press("ArrowRight", "ArrowRight", across > 0);
    press("ArrowLeft", "ArrowLeft", across < 0);

    // Fire while an enemy or a missile will be in line next frame, never at the spared enemy. Once the laser is warm,
    // only about the frames it does damage on; it cools before it overheats.
    const vx = across > 0 ? Math.min(1.6, player.vx + 0.09) : across < 0 ? Math.max(-1.6, player.vx - 0.09) : player.vx * 0.92;
    const gy = Math.max(3, Math.min(152, y + Math.sign(pick.c - y) * 3)) + 5, gx = Math.max(4, Math.min(120, x + vx)) + 15;
    const inLine = e => { const b = box(e); return b.x > gx && (b.x < W - 6 || e.kind === "asteroid") && gy >= b.y - 1 && gy <= b.y + b.h + 1; };
    const enemyInLine = enemies.some(e => !e.dead && e !== spare && inLine(e));
    const missileInLine = missiles.some(m => m.x > gx && Math.abs(m.y - gy) < 2);
    const toHit = (12 - (game.frame + 1) % 12) % 12;
    if (laser.heat > 85) hot = true;
    else if (laser.heat < 35) hot = false;
    press("Space", " ", !(spare && inLine(spare)) &&
      ((enemyInLine && !hot && (laser.heat < 30 || toHit <= 2)) || (missileInLine && laser.heat < 92)));
  }

  function frame() {
    requestAnimationFrame(frame);
    if (document.getElementById("start").style.display !== "none") return;   // not switched on yet
    // Waits are counted in the game's own frames, however many of them pass between two of these calls.
    if (game.state !== last) { last = game.state; since = game.frame || 0; }
    const waited = (game.frame || 0) - since;
    if (game.state !== "play" || game.paused || !player.alive) { releaseAll(); hot = false; }
    // Any key leaves the title for the attract screen, where fire starts the game; R plays again after it is over.
    if (game.state === "title" && waited >= 45) { tap("ArrowDown", "ArrowDown"); since = game.frame; }
    else if (game.state === "attract" && waited >= 45) { tap("Space", " "); since = game.frame; }
    else if (game.state === "gameover" && waited >= 120) { tap("KeyR", "r"); since = game.frame; }
    else if (game.state === "play" && !game.paused && player.alive) fly();
  }
  requestAnimationFrame(frame);
})();
