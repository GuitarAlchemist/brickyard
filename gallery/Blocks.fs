/// The hall's gallery as Lego blocks: the learn site's courses face out on a bookcase, its demos on a wall of screens
/// over a counter, its artifacts in frames, all against the hall's left wall under picture lights. The blocks describe
/// the furniture and where each title goes: one anchor a slot, at the centre of the face a title is lettered on, and
/// one named titre for the section's heading. The scene letters the titles, colours the covers and opens the links
/// (scene/HallGallery.cs). Lengths are in metres. A wall block's back is at z 0, against its wall, and its face looks down
/// +Z; its slots run along +X in reading order, top row first.
module Gallery.Blocks

open Lego
open Lego.Space

let private id name : BlockId = { Name = name; Version = 1 }

let private real name value lo hi : Param =
    { Name = name
      Default = Real value
      Min = lo
      Max = hi }

let private whole name value lo hi : Param =
    { Name = name
      Default = Whole value
      Min = float lo
      Max = float hi }

let private box x y z color = Box(v3 x y z, color)
let private place t part = Move(t, part)

// Linear RGB.
let private oak = v3 0.2 0.12 0.065
let private darkOak = v3 0.11 0.07 0.04
let private leather = v3 0.09 0.08 0.075
let private paper = v3 0.78 0.75 0.68
let private steel = v3 0.16 0.17 0.18
let private brass = v3 0.5 0.36 0.16
let private bezel = v3 0.03 0.03 0.035
let private screenBlue = v3 0.035 0.06 0.1
let private warm = v3 1.0 0.85 0.6

/// How many rows count slots take, cols a row.
let rows count cols = (count + cols - 1) / cols

// The bookcase's measures: its boards' thickness, and its cornice's height, which carries the heading.
let private board = 0.03
let private cornice = 0.12

/// The bookcase's width and height (its cornice's top) for count books, perShelf a shelf, each in a slot wide and a
/// row tall, its lowest shelf lowest metres up.
let bookcaseSize count perShelf slot row lowest =
    float perShelf * slot + 2.0 * board, lowest + float (rows count perShelf) * row + cornice

// The heading's board over a wall of screens or of frames: its height and how far it stands above the top row.
let private heading = 0.14
let private headingGap = 0.08

/// A wall of count slots, cols a row, each width by height with gap between them, the lowest bottom metres up: its
/// width and height (the heading board's top).
let wallSize count cols width height gap bottom =
    float cols * (width + gap), bottom + float (rows count cols) * (height + gap) - gap + headingGap + heading

/// A book standing face out: two leather boards, the pages between them and a spine on its left. Anchor face: the
/// front board's centre, where the scene puts its cover.
let livre: Block =
    { Id = id "livre"
      Params = [ real "width" 0.34 0.05 1.0; real "height" 0.28 0.05 1.0; real "thick" 0.035 0.01 0.2 ]
      Build =
        fun args _ ->
            let w, h, t = args.Real "width", args.Real "height", args.Real "thick"

            { Parts =
                [ place (at 0.0 0.005 0.0) (box (w - 0.012) (h - 0.01) (t - 0.006) paper)
                  place (at 0.0 0.0 (t / 2.0 - 0.002)) (box w h 0.004 leather)
                  place (at 0.0 0.0 (-t / 2.0 + 0.002)) (box w h 0.004 leather)
                  place (at (-w / 2.0 + 0.002) 0.0 0.0) (box 0.004 h t leather) ]
              Anchors = [ "face", at 0.0 (h / 2.0) (t / 2.0) ] } }

/// An oak bookcase of count books face out, perShelf a shelf: a plinth up to its lowest shelf, two sides, a back, a
/// shelf every row, and a cornice whose face carries the heading. Anchors livre-0 and on (the books' faces, top shelf
/// first, left to right) and titre (the cornice's face).
let bibliotheque: Block =
    { Id = id "bibliotheque"
      Params =
        [ whole "count" 41 1 120
          whole "perShelf" 11 1 30
          real "slot" 0.4 0.2 1.0
          real "row" 0.42 0.25 1.0
          real "base" 0.3 0.05 1.0
          real "depth" 0.3 0.15 0.8 ]
      Build =
        fun args context ->
            let count, perShelf = args.Whole "count", args.Whole "perShelf"
            let slot, row, lowest, depth = args.Real "slot", args.Real "row", args.Real "base", args.Real "depth"
            let shelves = rows count perShelf
            let width, height = bookcaseSize count perShelf slot row lowest
            let top = height - cornice
            let wood = oak * float32 (1.0 + context.Random.Between(-0.05, 0.05))
            let bookW, bookH, thick = slot - 0.08, row - 0.14, 0.035
            let bookZ = depth - 0.07

            let slotAt i =
                let r, c = i / perShelf, i % perShelf
                -width / 2.0 + board + (float c + 0.5) * slot, lowest + float (shelves - 1 - r) * row

            { Parts =
                [ place (at 0.0 0.0 (depth / 2.0)) (box (width - 2.0 * board) lowest depth darkOak)
                  for side in [ -1.0; 1.0 ] do
                      place (at (side * (width - board) / 2.0) 0.0 (depth / 2.0)) (box board top depth wood)
                  place (at 0.0 0.0 0.006) (box (width - 2.0 * board) top 0.012 (wood * 0.8f))
                  for r in 1 .. shelves - 1 do
                      place (at 0.0 (lowest + float r * row - board) (depth / 2.0)) (box (width - 2.0 * board) board depth wood)
                  place (at 0.0 top (depth / 2.0 + 0.01)) (box (width + 0.04) cornice (depth + 0.02) darkOak)
                  for i in 0 .. count - 1 do
                      let x, y = slotAt i
                      place (at x y bookZ) (Use($"livre{i}", id "livre", [ "width", Real bookW; "height", Real bookH; "thick", Real thick ])) ]
              Anchors =
                [ "titre", at 0.0 (top + cornice / 2.0) (depth + 0.02)
                  for i in 0 .. count - 1 do
                      let x, y = slotAt i
                      $"livre-{i}", at x (y + bookH / 2.0) (bookZ + thick / 2.0) ] } }

/// A heading board over a wall of slots: oak, width wide, its face carrying the heading.
let private headingBoard width top =
    place (at 0.0 (top - heading) 0.012) (box (min width 1.6) heading 0.024 darkOak)

/// The demos' wall: count screens, cols a row, each width by height in a black bezel over a counter counter metres
/// tall (none at 0), the lowest screen's bottom metres up. A screen glows faintly, as one that is on. Anchors ecran-0
/// and on (the screens' faces) and titre (the heading board's face).
let ecrans: Block =
    { Id = id "ecrans"
      Params =
        [ whole "count" 11 1 60
          whole "cols" 4 1 12
          real "width" 0.56 0.1 2.0
          real "height" 0.33 0.1 2.0
          real "gap" 0.06 0.0 1.0
          real "bottom" 0.95 0.0 3.0
          real "counter" 0.75 0.0 1.2 ]
      Build =
        fun args _ ->
            let count, cols = args.Whole "count", args.Whole "cols"
            let w, h, gap, bottom, counter = args.Real "width", args.Real "height", args.Real "gap", args.Real "bottom", args.Real "counter"
            let lines = rows count cols
            let width, top = wallSize count cols w h gap bottom

            let slotAt i =
                let r, c = i / cols, i % cols
                -width / 2.0 + (float c + 0.5) * (w + gap), bottom + float (lines - 1 - r) * (h + gap)

            { Parts =
                [ if counter > 0.0 then
                      Clad(Metal, place (at 0.0 0.0 0.16) (box (width + 0.04) (counter - 0.04) 0.32 steel))
                      place (at 0.0 (counter - 0.04) 0.18) (box (width + 0.1) 0.04 0.36 oak)
                  for i in 0 .. count - 1 do
                      let x, y = slotAt i
                      Clad(Metal, place (at x (y - 0.015) 0.015) (box (w + 0.03) (h + 0.03) 0.03 bezel))
                      Clad(Glow 0.6, place (at x y 0.031) (box w h 0.002 screenBlue))
                  headingBoard width top ]
              Anchors =
                [ "titre", at 0.0 (top - heading / 2.0) 0.024
                  for i in 0 .. count - 1 do
                      let x, y = slotAt i
                      $"ecran-{i}", at x (y + h / 2.0) 0.032 ] } }

/// The artifacts' wall: count frames, cols a row, each an oak moulding round a paper mat width by height, the lowest
/// mat's bottom metres up. Anchors cadre-0 and on (the mats' faces) and titre (the heading board's face).
let cadres: Block =
    { Id = id "cadres"
      Params =
        [ whole "count" 7 1 60
          whole "cols" 4 1 12
          real "width" 0.34 0.1 2.0
          real "height" 0.26 0.1 2.0
          real "gap" 0.06 0.0 1.0
          real "bottom" 1.05 0.0 3.0 ]
      Build =
        fun args _ ->
            let count, cols = args.Whole "count", args.Whole "cols"
            let w, h, gap, bottom = args.Real "width", args.Real "height", args.Real "gap", args.Real "bottom"
            let lines = rows count cols
            let width, top = wallSize count cols w h gap bottom
            let m = 0.025 // the moulding's width

            let slotAt i =
                let r, c = i / cols, i % cols
                -width / 2.0 + (float c + 0.5) * (w + gap), bottom + float (lines - 1 - r) * (h + gap)

            { Parts =
                [ for i in 0 .. count - 1 do
                      let x, y = slotAt i
                      place (at x y 0.005) (box w h 0.01 paper)
                      place (at x (y + h) 0.015) (box (w + 2.0 * m) m 0.03 oak)
                      place (at x (y - m) 0.015) (box (w + 2.0 * m) m 0.03 oak)
                      for side in [ -1.0; 1.0 ] do
                          place (at (x + side * (w + m) / 2.0) y 0.015) (box m h 0.03 oak)
                  headingBoard width top ]
              Anchors =
                [ "titre", at 0.0 (top - heading / 2.0) 0.024
                  for i in 0 .. count - 1 do
                      let x, y = slotAt i
                      $"cadre-{i}", at x (y + h / 2.0) 0.0105 ] } }

/// A picture light: two brass arms reach metres out from the wall, holding a hood over a warm bar length wide.
let applique: Block =
    { Id = id "applique"
      Params = [ real "length" 1.0 0.2 6.0; real "reach" 0.6 0.1 1.5 ]
      Build =
        fun args _ ->
            let length, reach = args.Real "length", args.Real "reach"

            { Parts =
                Clad(
                    Metal,
                    Group
                        [ for side in [ -1.0; 1.0 ] do
                              place (at (side * (length / 2.0 - 0.1)) 0.0 (reach / 2.0)) (box 0.02 0.02 reach brass)
                          place (at 0.0 -0.03 reach) (box length 0.05 0.08 brass) ]
                )
                :: [ Clad(Glow 1.2, place (at 0.0 -0.045 reach) (box (length - 0.04) 0.015 0.04 warm)) ]
              Anchors = [] } }

// The default slots, the learn site's on 2026-11-16 (scene/gallery.json).
let private bookcase count = bookcaseSize count 11 0.4 0.42 0.3
let private screens count = wallSize count 4 0.56 0.33 0.06 0.95
let private frames count = wallSize count 4 0.34 0.26 0.06 1.05

/// The gap between two sections, and the margin at each end of the wall.
let gap = 0.25
let margin = 0.12

/// The picture lights' height and reach, which the scene's Low shader lights the wall from (Gallery.Wash).
let lightHeight = 2.45
let lightReach = 0.75

/// The highest a section may stand: below the left wall's clerestory windows (scene/HallShell.cs).
let ceiling = 2.75

/// The gallery along a wall whose inner face is at x wall, running from z back to z front: from the back, the
/// courses' bookcase, the demos' screens and the artifacts' frames, each facing +X, under a picture light. A section
/// with nothing in it is left out. The wall too short for them all, or a section taller than the ceiling (too many rows),
/// fails, and Lego refuses the scene.
let galerie: Block =
    { Id = id "galerie"
      Params =
        [ whole "courses" 41 0 120
          whole "demos" 11 0 60
          whole "artifacts" 7 0 60
          real "wall" -6.25 -50.0 50.0
          real "back" -3.45 -50.0 50.0
          real "front" 6.0 -50.0 50.0 ]
      Build =
        fun args _ ->
            let wall, back, front = args.Real "wall", args.Real "back", args.Real "front"

            let sections =
                [ "cours", args.Whole "courses", bookcase, id "bibliotheque"
                  "demos", args.Whole "demos", screens, id "ecrans"
                  "artifacts", args.Whole "artifacts", frames, id "cadres" ]
                |> List.filter (fun (_, count, _, _) -> count > 0)

            let widths = sections |> List.map (fun (_, count, size, _) -> fst (size count))
            let needed = List.sum widths + gap * float (max 0 (widths.Length - 1)) + 2.0 * margin

            if needed > front - back then
                failwith $"the gallery needs {needed:F2} m of wall and has {front - back:F2}"

            for name, count, size, _ in sections do
                let _, height = size count

                if height > ceiling then
                    failwith $"the {name} would stand {height:F2} m tall, above the clerestory's sill at {ceiling:F2} m"

            // On the wall facing +X: the block's +Z turns to +X and its +X to -Z, so a viewer facing the wall reads it
            // from front to back. The sections start at the back.
            let onWall z = at wall 0.0 z |> turned 90.0

            let starts = widths |> List.scan (fun z w -> z + w + gap) (back + margin)

            { Parts =
                [ for (name, count, size, block), start, width in List.zip3 sections (List.take sections.Length starts) widths do
                      let centre = start + width / 2.0
                      place (onWall centre) (Use(name, block, [ "count", Whole count ]))
                      let _, height = size count
                      let y = max lightHeight (height + 0.25)
                      place (at wall y centre |> turned 90.0) (Use($"lampe-{name}", id "applique", [ "length", Real(min 2.4 (width - 0.2)); "reach", Real lightReach ])) ]
              Anchors = [] } }

/// A section of the gallery: its instance's name under the root, its slots' anchors (name-0 and on), and the face each
/// slot carries a title on, width by height (a book's cover, a screen, a frame's mat), as the blocks make them by
/// default.
type Section =
    { Name: string
      Anchor: string
      Width: float
      Height: float }

let sections =
    [| { Name = "cours"; Anchor = "livre"; Width = 0.32; Height = 0.28 }
       { Name = "demos"; Anchor = "ecran"; Width = 0.56; Height = 0.33 }
       { Name = "artifacts"; Anchor = "cadre"; Width = 0.34; Height = 0.26 } |]

/// Every block the gallery names.
let library =
    Library.Of [ livre; bibliotheque; ecrans; cadres; applique; galerie ]

/// The gallery for that many courses, demos and artifacts, generated and validated.
let describe (courses: int) (demos: int) (artifacts: int) =
    Api.Describe(library, "galerie", 1UL, [ $"courses={courses}"; $"demos={demos}"; $"artifacts={artifacts}" ], Limits.Standard)
