// PARSEC's pilot: each animation frame it reads the game's state (ship, enemies, missiles, ground, tunnel ceiling,
// laser heat) and plays by keys alone, as a player at the cabinet would. It starts the game from its title, flies to
// the height that lines its laser up with the most pressing enemy while keeping clear of craft, shots and ground,
// fires while something is in line and the laser is cool enough, and starts again when the game is over.
(() => {
  const ahead = 36;            // frames of the enemies' courses foreseen
  const home = 14;             // the ship keeps to the left, where it sees most of the screen
  const held = {};
  let last = "", wait = 0, focus = null, hot = false;

  const press = (code, key, down) => {
    if (!!held[code] !== down) { held[code] = down; __deltaKey(down ? "keydown" : "keyup", code, key); }
  };
  const tap = (code, key) => { __deltaKey("keydown", code, key); __deltaKey("keyup", code, key); };
  const releaseAll = () => { for (const code of Object.keys(held)) press(code, code === "Space" ? " " : code, false); };

  // Across, the ship goes back to its home column, braking before it, or chases a saucer (see fly). Where it will be
  // over the next frames is foreseen under the same steering, as the game moves it.
  let xs = [];
  const shipX = t => xs[Math.min(t, ahead)];
  const steer = (x, vx, chase) => {
    const want = chase ? 1.6 : Math.max(-1.6, Math.min(1.6, (home - x) * 0.08));
    return vx < want - 0.12 ? 1 : vx > want + 0.12 ? -1 : 0;
  };

  // Where an enemy will be after each of the next frames, as the game moves it (index 0 is now); short if it leaves.
  function course(e) {
    const box = enemyBox(e), out = [box], acc = 0.004 + game.level * 0.0004;
    let { x, y, spd, dir } = e, turned = e.reversed;
    for (let t = 1; t <= ahead; t++) {
      if (e.kind === "swooper") { spd = Math.min(4.6, spd + acc); if (y < e.targetY) y = Math.min(e.targetY, y + 0.9); x -= spd; }
      else if (e.kind === "ltf") { spd = Math.min(5.6, spd + acc * 1.5); x -= spd; }
      else if (e.kind === "satellite") { x -= spd; y += Math.sign(player.y - y) * Math.min(Math.abs(player.y - y), 0.45); }
      else if (e.kind === "saucer") {
        x += dir * spd;
        if (dir === 1 && x > shipX(t) + 34 && !turned) { dir = -1; spd += 1; turned = true; }
        if (dir === -1 && x < -24) break;
      }
      else if (e.kind !== "asteroid" && x > e.patrolX) x -= 1.6;   // cruisers stop at their patrol line
      if (x < -30) break;
      out.push({ x, y, w: box.w, h: box.h });
    }
    return out;
  }

  // The ground's top at a screen column: the tunnel's flat floor while the ship is under its ceiling, else the
  // landscape, also the one that comes back once the tunnel is passed.
  function groundAt(sx) {
    const t = terrain.tunnel, wx = Math.floor(game.scrollX || 0) + sx;
    if (t && game.tunnelX + player.x < t.w) return terrain.heightAt(wx);
    const strip = terrain.strips[0].h.length, all = strip * terrain.strips.length, at = ((wx % all) + all) % all;
    return terrain.strips[Math.floor(at / strip)].h[at % strip];
  }
  function roofAt(sx) {
    const c = terrain.ceilAt(Math.floor(game.tunnelX || 0) + sx);
    return c === null ? -1 : c;
  }

  function fly() {
    const { x, y } = player;
    // A saucer turns back 34 pixels ahead of the ship, so the ship chases one on its way out, which keeps it in the
    // laser's reach longer.
    const chase = x < 100 && enemies.some(e => e.kind === "saucer" && !e.dead && e.dir === 1 && !e.reversed);
    xs = [x];
    for (let t = 1, sx = x, vx = player.vx; t <= ahead; t++) {
      const d = steer(sx, vx, chase);
      vx = d > 0 ? Math.min(1.6, vx + 0.09) : d < 0 ? Math.max(-1.6, vx - 0.09) : vx * 0.92;
      sx = Math.max(4, Math.min(120, sx + vx));
      xs.push(sx);
    }
    let floor = 999, roof = -1;
    for (let sx = Math.floor(x); sx <= x + 18 + 30 * game.scrollSpd; sx++) { floor = Math.min(floor, groundAt(sx)); roof = Math.max(roof, roofAt(sx)); }
    const low = Math.max(3, roof + 2), high = Math.min(152, floor - 12);

    // Heights the ship must not be at, frame by frame, and heights that line its gun up with a target on the frames
    // the laser does damage (one in twelve).
    const dangers = [], targets = [];
    for (const e of enemies) {
      if (e.dead) continue;
      const path = course(e), spans = [];
      for (let t = 1; t < path.length; t++) {
        const b = path[t];
        if (b.x < shipX(t) + 17 && b.x + b.w > shipX(t)) spans.push([t, b.y - 10, b.y + b.h]);
      }
      if (spans.length) dangers.push(spans);
      const aims = [];
      for (let t = 1; t <= path.length; t++) {
        const b = path[t - 1];   // the laser strikes before the enemy moves
        if ((game.frame + t) % 12 === 0 && b.x > shipX(t) + 16 && (b.x < W - 6 || e.kind === "asteroid")) aims.push([t, b.y - 5, b.y + b.h - 5]);
      }
      const cruiser = e.kind === "urbite" || e.kind === "dramite" || e.kind === "bynite";
      const value = (e.kind === "asteroid" ? 100 : cruiser ? 500 : 400) + (W - path[0].x) * 0.6 + (e === focus ? 80 : 0);
      if (aims.length) targets.push({ e, value, aims });
    }
    for (const m of missiles) {
      const spans = [];
      for (let t = 1; t <= ahead; t++) {
        const mx = m.x + m.vx * t, my = m.y + m.vy * t;
        if (mx > shipX(t) && mx < shipX(t) + 17) spans.push([t, my - 10, my]);
      }
      if (spans.length) dangers.push(spans);
    }

    // A saucer flying back off the left edge comes in again at a random height between 24 and 130, a pixel a frame
    // faster each time it turns; once too fast to dodge, the ship waits for it above them all.
    const wild = enemies.some(e => e.kind === "saucer" && !e.dead && e.dir === -1 && e.spd >= 4.5) ? 1000 : 0;

    // The ship moves three pixels a frame, so it weighs each height it can reach exactly: get there, stay until its
    // shot, then slip out of whatever comes. A crash it could not slip out of in time weighs most, the shallowest
    // first when none can be escaped; one it could is only a mild nudge away.
    let best = y, bestScore = -Infinity, bestTarget = null;
    for (let k = -50; k <= 50; k++) {
      const c = y + 3 * k, arrive = Math.abs(k);
      if (c < 3 || c > 152) continue;
      const at = t => y + Math.max(-3 * t, Math.min(3 * t, c - y));
      const risk = stay => {
        let cost = 0;
        for (const spans of dangers) {
          for (const [t, a, b] of spans) {
            const sy = at(t), depth = Math.min(sy - a, b - sy);
            if (depth > 0) { cost += t <= stay + Math.ceil(depth / 3) ? 1e5 + 1000 * depth : 1500 / t; break; }
          }
        }
        return cost;
      };
      let score = -arrive * 2 - Math.abs(c - 70) * 0.3;
      if (c > 14 && c < 139) score -= wild;
      if (c < low) score -= 1e6 + (low - c) * 1e3;
      if (c > high) score -= 1e6 + (c - high) * 1e3;
      let gain = 0, aimed = null, shot = arrive;
      for (const target of targets) {
        const hit = target.aims.find(([t, a, b]) => t >= arrive && c >= a && c <= b);
        if (hit && target.value - 4 * hit[0] > gain) { gain = target.value - 4 * hit[0]; aimed = target.e; shot = hit[0]; }
      }
      const idle = score - risk(arrive), aiming = aimed ? score + gain - risk(shot) : -Infinity;
      if (Math.max(idle, aiming) > bestScore) { bestScore = Math.max(idle, aiming); best = c; bestTarget = aiming > idle ? aimed : null; }
    }
    focus = bestTarget;
    press("ArrowUp", "ArrowUp", best < y);
    press("ArrowDown", "ArrowDown", best > y);

    const across = steer(x, player.vx, chase);
    press("ArrowRight", "ArrowRight", across > 0);
    press("ArrowLeft", "ArrowLeft", across < 0);

    // Fire while an enemy or a missile will be in line next frame; let the laser cool before it overheats.
    const gy = Math.max(3, Math.min(152, y + Math.sign(best - y) * 3)) + 5, gx = shipX(1) + 15;
    const enemyInLine = enemies.some(e => {
      if (e.dead) return false;
      const b = enemyBox(e);
      return b.x > gx && (b.x < W - 6 || e.kind === "asteroid") && gy >= b.y - 1 && gy <= b.y + b.h + 1;
    });
    const missileInLine = missiles.some(m => m.x > gx && Math.abs(m.y - gy) < 2);
    if (laser.heat > 80) hot = true;
    else if (laser.heat < 35) hot = false;
    press("Space", " ", (enemyInLine && !hot) || (missileInLine && laser.heat < 92));
  }

  function frame() {
    requestAnimationFrame(frame);
    if (document.getElementById("start").style.display !== "none") return;   // not switched on yet
    wait = game.state === last ? wait + 1 : 0;
    last = game.state;
    if (game.state !== "play" || game.paused || !player.alive) { releaseAll(); hot = false; }
    // Any key leaves the title for the attract screen, where fire starts the game; R plays again after it is over.
    if (game.state === "title" && wait % 45 === 44) tap("ArrowDown", "ArrowDown");
    else if (game.state === "attract" && wait % 45 === 44) tap("Space", " ");
    else if (game.state === "gameover" && wait % 120 === 119) tap("KeyR", "r");
    else if (game.state === "play" && !game.paused && player.alive) fly();
  }
  requestAnimationFrame(frame);
})();
