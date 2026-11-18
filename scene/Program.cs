// Brickyard P0 (GuitarAlchemist/gaia#260): the robot you talk to, standing in a brick hall, drawn with Vulkan through
// BRUTAL's bindings. The brain (../brain) tells it what it is doing, one JSON line per change on a local TCP port:
// {"state":"idle"}, "listening", "thinking", or {"state":"speaking","seconds":S,"envelope":[...]} with the voice's
// loudness every 20 ms, which opens the mouth as the sentence plays.
//
//	scene [--listen 8805] [--seconds N] [--demo] [--shots DIR] [--shelf SECTION] [--hidden] [--size WxH] [--immediate] [--fifo] [--cap N] [--quality auto|flat|low] | --selftest
//
// --demo plays idle, listening, thinking, speaking, a compaction, each expression, the caption of a sentence let pass
// and the pairing card in a loop without the brain, while the context gauge fills, and --shots DIR saves a PNG of each. The scene
// waits for vsync unless --immediate: shown, through FIFO with each frame waiting for the last to be on screen
// (VK_KHR_present_wait; --present-wait hidden too; --reflex paces by NVIDIA Reflex instead, Reflex.cs), else through
// Mailbox when the GPU offers it (--mailbox), at twice
// the refresh of the window's screen unless --cap says otherwise, and through plain FIFO with --fifo or when hidden;
// --cap N limits it to N frames a second, 60 by
// default when hidden (nothing paces a hidden window), so that it never takes the GPU from a live view beside it.
// --size WxH sizes the window, to measure a hidden one at a live one's size. --seconds closes it.
// --quality picks the lighting, Low by default on a GPU and Flat, the first one, when the CPU draws; Low draws a cool
// haze that thickens with distance and thins with height, which B or --no-fog turns off. --view N starts
// the camera on a view (FreeCamera: 2 the agents' office, 3 Gaia, 4 the whole hall, 5 the clocks, 6 the arcade
// cabinet, 7 the knots' floor). H hides or shows the wall clocks of Ottawa and Paris (WallClocks), shown by default.
// --marins FILE reads the knots' floor's 21 sailors' knots (KnotFloor, brain -ix-marins), else
// %LOCALAPPDATA%\delta\ix-marins.json.
//
// The camera has the Observatory's controls (FreeCamera): it walks, sprints, jumps, crouches, slides and leans, flies with
// F, turns with the right button held or, after Ctrl and a click, with the mouse until the next Ctrl and click (a click
// meanwhile acts where the view aims); 1 to 5 fly to views, and with Shift film the view as the cinematic's shot of it
// and leave the camera there. 0 plays the cinematic (Cinematic), which any key or click
// ends; Tab opens the wheel (ToolWheel), empty for now, which slows the scene to a quarter; M opens the shelf, which Tab
// opened before the wheel took it; F1 shows the keys. Esc never moves the camera: with the wheel open it shuts the wheel,
// during the cinematic it ends it, and otherwise it closes the window as before. --cinematic plays the cinematic
// (--cinematic-from N from its shot N, --film-view N view N's shot alone) and --wheel opens the wheel once the splash
// is over, --fly-to N flies to a view
// half a second later and --keys shows the keys' card, for captures; --reduced-motion keeps cuts, no sway, dip or wider
// field.
//
// An arcade cabinet stands on the right (ArcadeCabinet): a click on it, or 6 then a click, walks up to it and starts
// PARSEC, a web game whose script runs here without a browser (ScriptScreen); every key is then the game's, and Esc
// leaves it. --arcade-play starts it once the splash is over and --arcade-url plays another page's #cv canvas, for captures.
//
// It opens on BRUTAL's logo and copyright notice for 2.6 s, as BRUTAL's licence asks (Splash.cs); --demo's states and
// --shots' times start after it, and --shots also saves the splash.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using SN = System.Numerics;

return Stage.Run(args);

[StructLayout(LayoutKind.Sequential)]
// A box's vertex: where it is, its face's normal, colour and material, the face's first axis (its second is the normal
// crossed with it) and the face's edge: where the vertex lies along both axes, -1 or 1, and the face's half-size along
// them, in metres.
internal struct Vertex(SN.Vector3 pos, SN.Vector3 normal, SN.Vector3 color, Material material, SN.Vector3 tangent, SN.Vector4 edge)
{
    public SN.Vector3 Pos = pos;
    public SN.Vector3 Normal = normal;
    public SN.Vector3 Color = color;
    public Material Material = material;
    public SN.Vector3 Tangent = tangent;
    public SN.Vector4 Edge = edge;
}

// What the Low shader reads once a frame, 48 bytes: the camera's position, the haze's density per metre (0: none),
// whether the photographed materials are there (1) or drawn in their flat colours (0), whether the shadows and the
// ambient occlusion are traced (1) or painted (0), and the video projected on the gallery's wall (GalleryVideo.Projection):
// its picture's layer and its strength (0: none), and where on the wall (Screen: its centre's z and y, its half width,
// the wall's x).
[StructLayout(LayoutKind.Sequential)]
internal struct Frame(SN.Vector3 eye, float haze, bool photographed, bool traced, GalleryVideo.Projection projection)
{
    public SN.Vector4 Eye = new(eye, haze);
    public SN.Vector4 Options = new(photographed ? 1f : 0f, traced ? 1f : 0f, projection.Layer, projection.Strength);
    public SN.Vector4 Screen = projection.Screen;
}

// What each draw pushes, 128 bytes: the part's clip transform, the three rows of its world transform (for the light)
// and its glow, a colour and a strength.
[StructLayout(LayoutKind.Sequential)]
internal struct Push(SN.Matrix4x4 world, SN.Matrix4x4 viewProj, SN.Vector4 glow)
{
    public SN.Matrix4x4 Mvp = world * viewProj;
    public SN.Vector4 Row0 = new(world.M11, world.M21, world.M31, world.M41);
    public SN.Vector4 Row1 = new(world.M12, world.M22, world.M32, world.M42);
    public SN.Vector4 Row2 = new(world.M13, world.M23, world.M33, world.M43);
    public SN.Vector4 Glow = glow;
}

// What each splash draw pushes: its rectangle in clip space (left, top, right, bottom) and a tint whose alpha fades it.
[StructLayout(LayoutKind.Sequential)]
internal struct SplashPush(SN.Vector4 rect, SN.Vector4 tint)
{
    public SN.Vector4 Rect = rect;
    public SN.Vector4 Tint = tint;
}

internal static unsafe class Stage
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int FramesInFlight = 2;
    private const int ApiVersion13 = (1 << 22) | (3 << 12);
    private const VkShaderStageFlags PushStages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private const string VertexShader = """
        #version 450
        layout(location = 0) in vec3 inPos;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec3 inColor;
        layout(location = 3) in uint inMaterial;
        layout(location = 4) in vec3 inTangent;
        layout(location = 5) in vec4 inEdge;
        layout(push_constant) uniform Push { mat4 mvp; vec4 row0; vec4 row1; vec4 row2; vec4 glow; } pc;
        layout(location = 0) out vec3 vNormal;
        layout(location = 1) out vec3 vColor;
        layout(location = 2) out vec3 vWorld;
        layout(location = 3) flat out uint vMaterial;
        layout(location = 4) out vec3 vTangent;
        layout(location = 5) out vec4 vEdge;
        void main()
        {
            vec4 p = vec4(inPos, 1.0);
            gl_Position = pc.mvp * p;
            vWorld = vec3(dot(pc.row0, p), dot(pc.row1, p), dot(pc.row2, p));
            vNormal = vec3(dot(pc.row0.xyz, inNormal), dot(pc.row1.xyz, inNormal), dot(pc.row2.xyz, inNormal));
            vColor = inColor;
            vMaterial = inMaterial;
            vTangent = vec3(dot(pc.row0.xyz, inTangent), dot(pc.row1.xyz, inTangent), dot(pc.row2.xyz, inTangent));
            vEdge = inEdge;
        }
        """;

    private const string FragmentShader = """
        #version 450
        layout(location = 0) in vec3 vNormal;
        layout(location = 1) in vec3 vColor;
        layout(location = 2) in vec3 vWorld;
        layout(push_constant) uniform Push { mat4 mvp; vec4 row0; vec4 row1; vec4 row2; vec4 glow; } pc;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec3 n = normalize(vNormal);
            // A warm key light high on the left, a cold fill from the right, the floor's bounce from below.
            float key = max(dot(n, normalize(vec3(-0.45, 0.8, 0.55))), 0.0);
            float fill = max(dot(n, normalize(vec3(0.7, 0.25, 0.3))), 0.0);
            float bounce = 0.5 - 0.5 * n.y;
            vec3 light = vec3(1.0, 0.86, 0.68) * key + vec3(0.35, 0.45, 0.6) * 0.35 * fill
                + vec3(0.12, 0.1, 0.09) * bounce + 0.03;
            // A pool of light around the robot; the hall falls into the dark beyond it and above it.
            float pool = 1.0 - smoothstep(0.8, 6.0, length(vWorld.xz - vec2(0.0, -0.3)));
            float high = 1.0 - 0.6 * smoothstep(1.6, 4.5, vWorld.y);
            outColor = vec4(vColor * light * (0.1 + 0.9 * pool) * high + pc.glow.rgb * pc.glow.a, 1.0);
        }
        """;

    // The Low lighting: Flat's three lights, with rounded edges, each material's highlights and reflections of the hall
    // (Material in Mesh.cs), the robot's shadow and the dark where things meet the floor, and a shoulder for what grows
    // too bright.
    internal const string LowFragmentShader = """
        #version 450
        layout(location = 0) in vec3 vNormal;
        layout(location = 1) in vec3 vColor;
        layout(location = 2) in vec3 vWorld;
        layout(location = 3) flat in uint vMaterial;
        layout(location = 4) in vec3 vTangent;
        layout(location = 5) in vec4 vEdge;
        layout(push_constant) uniform Push { mat4 mvp; vec4 row0; vec4 row1; vec4 row2; vec4 glow; } pc;
        layout(set = 0, binding = 0) uniform Frame { vec4 eye; vec4 options; vec4 screen; } frame;
        layout(set = 0, binding = 1) uniform sampler2DArray albedoMaps; // colour, the height in alpha (MaterialTextures)
        layout(set = 0, binding = 2) uniform sampler2DArray normalMaps; // tangent space, green up
        layout(set = 0, binding = 3) uniform sampler2DArray armMaps;    // occlusion, roughness, metalness
        layout(set = 0, binding = 5) uniform sampler2DArray globeMaps;  // GlobeImages: a globe's west and east halves
        layout(set = 0, binding = 6) uniform sampler2DArray pictureMaps; // GalleryPictures: what the gallery's glasses show
        layout(set = 0, binding = 7) uniform sampler2DArray aerialMaps;  // GoogleStreets: the 3D tiles' photographs, a layer each
        layout(location = 0) out vec4 outColor;

        // Per photographed material (Material.BrickWall = 8 and on): its layer, its tile in metres (tools/textures), the
        // depth of its relief in metres, and 1 when only its grain is taken, on the surface's own colour (ClayBrick).
        const vec4 scans[6] = vec4[](
            vec4(0.0, 2.7, 0.023, 0.0),   // brick wall, laid by tools/textures every 2.7 m: 23 mm from the proudest brick to the deepest joint
            vec4(1.0, 3.0, 0.003, 0.0),   // worn concrete floor
            vec4(2.0, 3.0, 0.004, 0.0),   // cast concrete
            vec4(3.0, 0.5, 0.003, 0.0),   // pallet wood
            vec4(4.0, 1.0, 0.0008, 0.0),  // painted steel
            vec4(2.0, 0.6, 0.002, 1.0));  // clay brick: the cast concrete's grain on its colour

        // Where a box's face p, of outward normal face, is on a scan tiled every metres: the image's coordinates, and the
        // world directions of its u and of its up (green in the normal map). Walls keep the image upright.
        void project(vec3 p, vec3 face, float metres, out vec2 uv, out vec3 t, out vec3 b)
        {
            vec3 a = abs(face);
            if (a.y >= a.x && a.y >= a.z)
            {
                float s = face.y < 0.0 ? -1.0 : 1.0;
                uv = vec2(p.x, s * p.z) / metres;
                t = vec3(1.0, 0.0, 0.0);
                b = vec3(0.0, 0.0, -s);
            }
            else if (a.x >= a.z)
            {
                float s = face.x < 0.0 ? -1.0 : 1.0;
                uv = vec2(-s * p.z, -p.y) / metres;
                t = vec3(0.0, 0.0, -s);
                b = vec3(0.0, 1.0, 0.0);
            }
            else
            {
                float s = face.z < 0.0 ? -1.0 : 1.0;
                uv = vec2(s * p.x, -p.y) / metres;
                t = vec3(s, 0.0, 0.0);
                b = vec3(0.0, 1.0, 0.0);
            }
        }

        // Parallax occlusion: the point of the relief the eye sees through uv, marching into the scan's height (alpha,
        // 1 at the face) by depth tiles at most, finer where the view grazes the face.
        vec2 parallax(vec2 uv, vec3 v, vec3 t, vec3 b, vec3 face, float layer, float depth, vec2 dx, vec2 dy)
        {
            vec3 tv = vec3(dot(v, t), dot(v, b), dot(v, face));
            vec2 shift = vec2(tv.x, -tv.y) / max(tv.z, 0.2) * depth;
            int steps = int(mix(24.0, 8.0, clamp(tv.z, 0.0, 1.0)));
            float step = 1.0 / float(steps);
            vec2 duv = shift * step;
            float into = 0.0;
            vec2 at = uv;
            float below = 1.0 - textureGrad(albedoMaps, vec3(at, layer), dx, dy).a;
            for (int i = 0; i < 24 && i < steps && into < below; i++)
            {
                at -= duv;
                into += step;
                below = 1.0 - textureGrad(albedoMaps, vec3(at, layer), dx, dy).a;
            }

            vec2 last = at + duv;
            float after = below - into;
            float before = (1.0 - textureGrad(albedoMaps, vec3(last, layer), dx, dy).a) - (into - step);
            float w = after / min(after - before, -1e-5);
            return mix(at, last, clamp(w, 0.0, 1.0));
        }

        #ifdef RAYS
        layout(set = 0, binding = 4) uniform accelerationStructureEXT hallRays; // RayScene: last frame's meshes

        // reach, through the instances whose mask meets the given one (RayScene: 0x01 casts shadows, 0x02 the shell,
        // 0x04 the city, whose shadows only fall outside).
        float reachThrough(vec3 origin, vec3 dir, float tmax, bool any, uint mask)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, hallRays, gl_RayFlagsOpaqueEXT | (any ? gl_RayFlagsTerminateOnFirstHitEXT : 0u), mask, origin, 0.0, dir, tmax);
            while (rayQueryProceedEXT(q))
            {
            }

            return rayQueryGetIntersectionTypeEXT(q, true) == gl_RayQueryCommittedIntersectionNoneEXT ? tmax : rayQueryGetIntersectionTEXT(q, true);
        }

        // How far along dir, from origin, the first surface is; tmax when none is nearer.
        float reach(vec3 origin, vec3 dir, float tmax, bool any)
        {
            return reachThrough(origin, dir, tmax, any, any ? 0x01u : 0xFFu); // a shadow ray passes the shell (RayScene.ShadowMask)
        }

        // Interleaved gradient noise: a different turn for each pixel, the same every frame, so nothing crawls.
        float grain(vec2 p)
        {
            return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
        }

        void across(vec3 n, out vec3 t, out vec3 b)
        {
            t = normalize(cross(n, abs(n.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
            b = cross(n, t);
        }

        // The city of Ottawa's draw (OttawaStreets), which Program marks with a glow of no light (alpha -1).
        bool city()
        {
            return pc.glow.a < -0.5;
        }

        // Out in the streets: the city, wherever it stands round the hall, and whatever lies past the front wall.
        bool outdoors(vec3 p)
        {
            return city() || p.z > 6.0;
        }

        // The key light's share that reaches p: three rays into a light about 4° across, each pixel's turned its way.
        float keyShadow(vec3 p, vec3 face, vec3 keyDir)
        {
            if (dot(face, keyDir) < -0.05)
            {
                return 0.0;
            }

            vec3 t, b;
            across(keyDir, t, b);
            float turn = grain(gl_FragCoord.xy) * 6.2831853;
            float lit = 0.0;
            // Outside the hall's front wall (HallShell.Front, 6.0) the streets: the city's buildings cast shadows there,
            // and from further away (a 60 m tower's shadow runs about 70 m, 45 of the hall's units).
            bool street = outdoors(p);
            float upTo = street ? 160.0 : 40.0;
            uint mask = street ? 0x05u : 0x01u;
            for (int i = 0; i < 3; i++)
            {
                float a = turn + float(i) * 2.3999632;
                float r = sqrt((float(i) + 0.5) / 3.0) * 0.035;
                lit += reachThrough(p, normalize(keyDir + (cos(a) * t + sin(a) * b) * r), upTo, true, mask) >= upTo ? 1.0 : 0.0;
            }

            return lit / 3.0;
        }

        // How open p is to the hall around it: five rays over the hemisphere, cosine-weighted, each counting how far it
        // goes within 0.7 m; 1 in the open, less in corners, under things and between them.
        float openness(vec3 p, vec3 face)
        {
            vec3 t, b;
            across(face, t, b);
            float turn = grain(gl_FragCoord.yx + 31.0) * 6.2831853;
            float shift = grain(gl_FragCoord.xy + 7.0);
            float open = 0.0;
            for (int i = 0; i < 5; i++)
            {
                float u = (float(i) + shift) / 5.0;
                float a = turn + float(i) * 2.3999632;
                vec3 d = normalize(t * (cos(a) * sqrt(u)) + b * (sin(a) * sqrt(u)) + face * sqrt(1.0 - u));
                open += reach(p, d, 0.7, false) / 0.7;
            }

            return open / 5.0;
        }
        #endif

        // Per material: its reflectance head-on (a metal's is its colour), its gloss (Blinn-Phong's exponent), whether
        // it is a metal, and how much a grazing view adds to its reflection, little on rough surfaces.
        const vec4 materials[8] = vec4[](
            vec4(0.02, 6.0, 0.0, 0.12),   // matte: brick, concrete, wood
            vec4(0.05, 90.0, 0.0, 0.8),   // enamel and paint
            vec4(0.0, 40.0, 1.0, 1.0),    // brushed aluminium
            vec4(0.0, 24.0, 1.0, 0.8),    // steel
            vec4(0.02, 4.0, 0.0, 0.1),    // rubber
            vec4(0.04, 260.0, 0.0, 1.0),  // glass, screens and lamps
            vec4(0.03, 5.0, 0.0, 0.35),   // hemp rope: rough, a little sheen at grazing
            vec4(0.04, 36.0, 0.0, 0.55)); // turned oak, oiled

        // The distance from p to a rectangle of half-size h about the origin, negative inside.
        float box(vec2 p, vec2 h)
        {
            vec2 d = abs(p) - h;
            return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
        }

        // Value noise, smooth between whole points, 0 to 1.
        float hash(vec3 p)
        {
            p = fract(p * 0.3183099 + 0.1) * 17.0;
            return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
        }

        float noise(vec3 x)
        {
            vec3 i = floor(x);
            vec3 f = fract(x);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(mix(hash(i), hash(i + vec3(1, 0, 0)), f.x), mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), f.x), f.y),
                       mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), f.x), mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
        }

        // A highlight's lobe about the half-vector h: Blinn-Phong of exponent e; on brushed metal (aniso above 0)
        // stretched along the brush t, as Ashikhmin and Shirley's is, wide along the grooves and sharp across them,
        // its energy kept.
        float lobe(vec3 n, vec3 h, float e, vec3 t, float aniso)
        {
            float nh = max(dot(n, h), 0.0);
            if (aniso <= 0.0)
            {
                return pow(nh, e);
            }

            vec3 b = normalize(cross(n, t));
            t = cross(b, n);
            float eu = e * (1.0 - aniso);
            float ev = e / (1.0 - aniso);
            float ht = dot(h, t);
            float hb = dot(h, b);
            return sqrt((eu + 1.0) * (ev + 1.0)) / (e + 1.0) * pow(nh, (eu * ht * ht + ev * hb * hb) / max(1.0 - nh * nh, 1e-4));
        }

        // What the hall shows a glossy surface in direction r: the lit floor below, the brick around, the dark roof
        // above, and less of all of it behind the camera, where nothing is built.
        vec3 hall(vec3 r)
        {
            vec3 ground = vec3(0.11, 0.095, 0.08);
            vec3 wall = vec3(0.06, 0.035, 0.025);
            vec3 roof = vec3(0.012, 0.013, 0.016);
            vec3 c = r.y < 0.0 ? mix(wall, ground, smoothstep(0.0, -0.35, r.y)) : mix(wall, roof, smoothstep(0.05, 0.6, r.y));
            return c * (r.z < 0.0 ? 1.0 : 0.7);
        }

        // What a surface at p mirrors along r: inside, the hall; out in the streets of Ottawa, the day's sky above and
        // the paved ground below.
        vec3 around(vec3 r, vec3 p)
        {
            return outdoors(p) ? (r.y < 0.0 ? vec3(0.16, 0.15, 0.14) : mix(vec3(0.62, 0.68, 0.76), vec3(0.42, 0.55, 0.75), smoothstep(0.0, 0.5, r.y))) * 0.5 : hall(r);
        }

        // The haze between the eye and p: none in the first 3 m, then thicker with distance and thinner with height
        // (half as thick 2 m up), a cool steel blue, dim, so that the far brick and the roof recede into it while what
        // is near stays clear. Its colour is HazeColor's, the clear colour beyond the hall.
        vec3 haze(vec3 c, vec3 p)
        {
            float density = frame.eye.w * exp(-0.35 * max(0.5 * (p.y + frame.eye.y), 0.0));
            float through = exp(-density * max(length(p - frame.eye.xyz) - 3.0, 0.0));
            // Inside, the hall's dusk; seen from the streets or looking out onto them, a day's blue (OttawaStreets).
            float day = max(smoothstep(6.0, 6.4, frame.eye.z), city() ? 1.0 : smoothstep(6.0, 6.4, p.z));
            return mix(mix(vec3(0.075, 0.09, 0.115), vec3(0.42, 0.55, 0.75), day), c, through);
        }

        // Below 0.6 as it is; above, rolling off towards 1 instead of clipping.
        vec3 shoulder(vec3 x)
        {
            const float a = 0.6;
            return mix(x, a + (1.0 - a) * (1.0 - exp(-(x - a) / (1.0 - a))), step(a, x));
        }

        void main()
        {
            vec3 face = normalize(vNormal);
            vec3 v = normalize(frame.eye.xyz - vWorld);
            vec3 base = vColor;
            float occlusion = 1.0;
            float skyGlass = 0.0; // a city window's glass, which mirrors the sky (Material.Facade)
            float aerial = 0.0;   // Google's photographs, which carry their own light (Material.Aerial)
            vec3 n;
            vec4 m;
            if (vMaterial == 16u)
            {
                // A module's triangle painted with a globe (Material.Imaged): Edge holds its place, u from 180° W, v from
                // the north pole, and the globe's first layer, its west half; the east half is the next. Water, bluer
                // than it is red, takes a little gloss.
                vec2 uv = vEdge.xy * vec2(2.0, 1.0);
                vec2 dx = dFdx(uv), dy = dFdy(uv);
                float east = uv.x >= 1.0 ? 1.0 : 0.0;
                base = textureGrad(globeMaps, vec3(uv.x - east, uv.y, vEdge.z + east), dx, dy).rgb;
                n = face;
                float water = smoothstep(0.02, 0.08, base.b - base.r);
                m = mix(materials[0], vec4(0.02, 70.0, 0.0, 0.7), water);
            }
            else if (vMaterial == 17u)
            {
                // A gallery picture (Material.Pictured): Edge holds its place on it and its layer, printed on satin
                // paper and lit a little from within, as a diorama's backdrop is.
                vec2 uv = vEdge.xy;
                base = textureGrad(pictureMaps, vec3(uv, vEdge.z), dFdx(uv), dFdy(uv)).rgb;
                n = face;
                m = vec4(0.03, 30.0, 0.0, 0.35);
            }
            else if (vMaterial >= 8u && vMaterial < 16u && frame.options.x > 0.5)
            {
                // A photographed material: its scan, its relief, its normals and its roughness; no rounded edges.
                vec4 s = scans[min(vMaterial - 8u, 5u)];
                vec2 uv;
                vec3 t, b;
                project(vWorld, face, s.y, uv, t, b);
                vec2 dx = dFdx(uv), dy = dFdy(uv);
                uv = parallax(uv, v, t, b, face, s.x, s.z / s.y, dx, dy);
                vec3 colour = textureGrad(albedoMaps, vec3(uv, s.x), dx, dy).rgb;
                vec3 tn = textureGrad(normalMaps, vec3(uv, s.x), dx, dy).xyz * 2.0 - 1.0;
                vec3 arm = textureGrad(armMaps, vec3(uv, s.x), dx, dy).xyz;
                n = normalize(t * tn.x + b * tn.y + face * max(tn.z, 0.05));
                base = s.w > 0.5 ? vColor * clamp(dot(colour, vec3(0.2126, 0.7152, 0.0722)) * 3.0, 0.55, 1.45) : colour * vColor;
                occlusion = arm.x;
                float r = clamp(arm.y, 0.05, 1.0);
                m = vec4(0.04, clamp(2.0 / (r * r * r * r) - 2.0, 2.0, 600.0), arm.z, mix(1.0, 0.1, r));
            }
            else if (vMaterial == 6u)
            {
                // A laid rope's strand (Material.Rope; Edge: along it in metres, round it, its radius, a seed): four
                // yarns twisted against the strand's lay, each a ridge that the light catches, the grooves between them
                // darker, each yarn its own tone, and the fibres along it. Where a yarn is under two pixels the relief
                // fades to the strand's own round, so that nothing shimmers.
                vec3 t = normalize(vTangent - face * dot(vTangent, face));
                vec3 circum = cross(t, face);
                float rs = max(vEdge.z, 1e-4);
                float pitch = rs * 9.0;
                float phase = 4.0 * vEdge.y + 6.2831853 * vEdge.x / pitch;
                float detail = 1.0 - smoothstep(0.6, 1.6, fwidth(phase));
                vec3 slope = circum * (4.0 / rs) + t * (6.2831853 / pitch);
                n = normalize(face + 0.15 * rs * sin(phase) * slope * detail);
                float crest = 0.5 + 0.5 * cos(phase);
                float yarn = floor(phase / 6.2831853);
                float tone = 0.9 + 0.12 * fract(sin(yarn * 12.9898 + vEdge.w * 78.233) * 43758.5453);
                float fibre = phase * 6.0 + 1.7 * sin(vEdge.x / rs * 3.1 + vEdge.w);
                float fibres = 1.0 + 0.09 * sin(fibre) * (1.0 - smoothstep(0.8, 2.0, fwidth(fibre)));
                base = vColor * mix(1.0, tone * fibres, detail) * mix(0.8, mix(0.6, 1.06, crest), detail);
                occlusion = mix(1.0, mix(0.72, 1.0, crest), detail);
                m = materials[6];
            }
            else if (vMaterial == 20u)
            {
                // A triangle of Google's Photorealistic 3D Tiles (Material.Aerial): its tile's photograph, at its place
                // on it, drawn as it is (below, unlit).
                base = texture(aerialMaps, vec3(vEdge.xy, vEdge.z)).rgb;
                n = face;
                m = materials[0];
                aerial = 1.0;
            }
            else if (vMaterial == 19u)
            {
                // A city building's wall (Material.Facade, OttawaStreets): a window every 2.2 m along it (1.42 at the
                // hall's scale) and on every 3 m floor (1.94), the ground floor's taller; dark glass, each pane its own
                // shade, in the wall's stone, which takes the sky's light further on. Under a few pixels a window
                // fades to the wall's tone.
                vec3 along = normalize(vTangent - face * dot(vTangent, face));
                vec2 q = vec2(dot(vWorld, along) / 1.42, vWorld.y / 1.94);
                vec2 w = fwidth(q);
                float detail = 1.0 - smoothstep(0.12, 0.35, max(w.x, w.y));
                vec2 cell = fract(q);
                vec2 lo = vec2(0.24, q.y < 1.0 ? 0.08 : 0.3), hi = vec2(0.76, 0.84);
                vec2 inside = smoothstep(lo - w, lo + w, cell) * (1.0 - smoothstep(hi - w, hi + w, cell));
                float glass = inside.x * inside.y * detail;
                float pane = fract(sin(dot(floor(q), vec2(12.9898, 78.233)) + vEdge.w * 91.7) * 43758.5453);
                float sill = (1.0 - smoothstep(0.0, w.y * 1.5, abs(cell.y - lo.y + 0.02))) * step(lo.x, cell.x) * step(cell.x, hi.x) * detail;
                base = mix(vColor * mix(1.0, 0.82, sill), vec3(0.035, 0.045, 0.055) * (0.7 + 0.6 * pane), glass);
                n = face;
                m = mix(materials[0], vec4(0.04, 220.0, 0.0, 1.0), glass);
                skyGlass = glass;
            }
            else if (vMaterial == 7u)
            {
                // Turned oak (Material.Wood): its grain along the post, the lines wandering a little, darker late wood.
                float rs = max(vEdge.z, 1e-4);
                float across = vEdge.y * rs / 0.0016 + 2.5 * sin(vEdge.x * 9.0 + vEdge.w) + 0.8 * sin(vEdge.x * 37.0 + vEdge.y * 3.0);
                float lines = 1.0 - smoothstep(0.6, 1.4, fwidth(across));
                float late = smoothstep(0.55, 0.95, 0.5 + 0.5 * sin(across * 6.2831853));
                float streak = 0.94 + 0.06 * sin(vEdge.x * 61.0 + vEdge.w * 5.0);
                base = vColor * streak * mix(1.0, mix(1.05, 0.72, late), lines);
                n = face;
                m = materials[7];
            }
            else
            {
                // Rounded edges: within 8 mm of a face's edge, less on thin parts and never under a pixel, the normal
                // leans towards the next face, halfway there at the edge itself, so that the edges catch the light.
                vec3 across = normalize(vTangent - face * dot(vTangent, face));
                vec2 toEdge = (1.0 - abs(vEdge.xy)) * vEdge.zw;
                vec2 width = max(min(vec2(0.008), 0.35 * vEdge.zw), fwidth(toEdge));
                vec2 lean = (1.0 - smoothstep(vec2(0.0), width, toEdge)) * sign(vEdge.xy);
                n = normalize(face + lean.x * across + lean.y * cross(face, across));
                m = materials[vMaterial >= 8u ? 0u : min(vMaterial, 7u)];
                if (vMaterial == 18u)
                {
                    // Oak boards (Material.Oak): flat-sawn grain along the face's longer side (Edge: where on the
                    // face, its half sizes), the late wood darker, the pores in fine dark streaks, and the figure's
                    // sheen sliding as the eye moves (chatoyance); oiled, under the clear coat further on. Each line
                    // fades to its tone where it would be under a pixel.
                    bool longU = vEdge.z >= vEdge.w;
                    vec2 q = longU ? vEdge.xy * vEdge.zw : vEdge.yx * vEdge.wz; // metres along the grain, across it
                    vec3 grain = longU ? across : cross(face, across);
                    float seed = fract(dot(vEdge.zw, vec2(91.7, 37.3)));
                    float ring = q.y / 0.005 + 6.0 * noise(vec3(q.x * 1.5, q.y * 12.0, seed * 50.0)) + 1.5 * sin(q.x * 9.0 + q.y * 30.0 + seed * 40.0);
                    float rings = 1.0 - smoothstep(0.5, 1.5, fwidth(ring));
                    float late = smoothstep(0.35, 0.95, 0.5 + 0.5 * sin(ring * 6.2831853)) * mix(0.5, 1.0, noise(vec3(q.x * 4.0, ring * 0.5, seed * 7.0)));
                    float pore = smoothstep(0.62, 0.8, noise(vec3(q.x * 30.0, q.y * 1400.0, seed * 9.0)));
                    float pores = 1.0 - smoothstep(0.5, 1.5, fwidth(q.y * 1400.0));
                    float sheen = 1.0 - smoothstep(0.5, 1.5, fwidth(q.y * 260.0));
                    float figure = 1.0 + 0.1 * sin(q.y * 260.0 + 7.0 * dot(v, grain) + seed * 20.0) * sheen;
                    float board = 0.88 + 0.24 * noise(vec3(q.x * 0.8, q.y * 3.0, seed * 31.0)); // a board's tone, not even
                    base = vColor * board * figure * mix(1.0, mix(1.06, 0.78, late), rings) * mix(1.0, 0.8, pore * pores);
                    m = vec4(0.04, 28.0, 0.0, 0.4);
                }
                else if (vMaterial == 2u || vMaterial == 3u)
                {
                    // Worn metal: its polish varies over centimetres and over millimetres, duller where hands have
                    // been, darker with it; the fine wear fades where it would be under a pixel.
                    float fine = 1.0 - smoothstep(0.3, 1.0, length(fwidth(vWorld)) * 160.0);
                    float wear = 0.6 * noise(vWorld * 23.0) + 0.4 * mix(0.5, noise(vWorld * 160.0), fine);
                    m.y *= mix(0.45, 1.5, wear);
                    base *= mix(0.85, 1.06, wear);
                }
            }

            vec3 keyDir = normalize(vec3(-0.45, 0.8, 0.55));
            vec3 fillDir = normalize(vec3(0.7, 0.25, 0.3));
            vec3 keyColor = vec3(1.0, 0.86, 0.68);
            vec3 fillColor = vec3(0.35, 0.45, 0.6) * 0.35;
            if (outdoors(vWorld))
            {
                // The streets of Ottawa, past the hall's front wall: an afternoon sun from the south-west, behind the eye
                // that looks out of the door towards Parliament (+z is northward there), and the blue sky's light from above.
                keyDir = normalize(vec3(0.35, 0.7, -0.6));
                fillDir = vec3(0.0, 1.0, 0.0);
                keyColor = vec3(1.0, 0.93, 0.82) * 1.1;
                fillColor = vec3(0.42, 0.55, 0.75) * 0.6;
            }

            // On the floor: the robot's shadow from the key light, as a column 1.25 m high whose penumbra widens away
            // from its foot, and the dark where the robot, the pallet, the cabinet and the wall stand. On the wall: the
            // dark where it meets the floor.
            float shade = 1.0;
            float ao = 1.0;
            #ifdef RAYS
            if (frame.options.y > 0.5)
            {
                // Traced: whatever stands between the surface and the key light shades it, and whatever is near it
                // keeps the hall's light from it, each mesh as it is drawn.
                vec3 p = vWorld + face * 0.004;
                shade = keyShadow(p, face, keyDir);
                ao = mix(0.3, 1.0, openness(p, face));
            }
            else
            #endif
            if (face.y > 0.9 && vWorld.y < 0.01)
            {
                vec2 p = vWorld.xz;
                vec2 reach = -keyDir.xz / keyDir.y * 1.25;
                float t = clamp(dot(p, reach) / dot(reach, reach), 0.0, 1.0);
                float soft = 0.04 + 0.3 * t;
                shade = mix(1.0, smoothstep(-soft, soft, box(p - t * reach, vec2(0.24, 0.19))), 0.8);
                ao = mix(0.4, 1.0, smoothstep(0.0, 0.22, box(p, vec2(0.27, 0.28))))
                    * mix(0.55, 1.0, smoothstep(0.0, 0.18, box(p - vec2(-2.1, -1.5), vec2(0.5, 0.4))))
                    * mix(0.5, 1.0, smoothstep(0.0, 0.2, box(p - vec2(2.2, -1.9), vec2(0.45, 0.25))))
                    * mix(0.55, 1.0, smoothstep(0.0, 0.45, p.y + 3.3));
            }
            else if (vWorld.z < -3.25)
            {
                ao = mix(0.55, 1.0, smoothstep(0.0, 0.4, vWorld.y));
            }

            float key = max(dot(n, keyDir), 0.0);
            // Outside, the sky's light from the whole dome: a wall sees half of it, a roof all, the shaded side too.
            float fill = outdoors(vWorld) ? 0.5 + 0.5 * n.y : max(dot(n, fillDir), 0.0);
            float bounce = 0.5 - 0.5 * n.y;
            ao *= occlusion;
            vec3 light = (keyColor * key * shade + fillColor * fill + vec3(0.12, 0.1, 0.09) * bounce + 0.03) * ao;

            // The gallery's picture lights (HallGallery.Wash): a warm wash from a line along the left wall, 0.75 m out
            // and 2.45 m up, on what faces it near that wall, falling off with distance; untraced, so nothing shadows it.
            vec3 washAt = vec3(-5.5, 2.45, clamp(vWorld.z, -3.3, 5.9));
            vec3 toWash = washAt - vWorld;
            float washD = max(length(toWash), 0.05);
            light += vec3(1.0, 0.86, 0.66) * 0.75 * max(dot(n, toWash / washD), 0.0) / (1.0 + 0.8 * washD * washD) * (1.0 - smoothstep(-5.4, -4.2, vWorld.x)) * ao;
            // And Streeling's card catalogue on the back wall, from its own light 0.6 m out (HallGallery.BackWash).
            vec3 backAt = vec3(clamp(vWorld.x, -5.7, -3.8), 2.4, -2.85);
            vec3 toBack = backAt - vWorld;
            float backD = max(length(toBack), 0.05);
            light += vec3(1.0, 0.86, 0.66) * 0.75 * max(dot(n, toBack / backD), 0.0) / (1.0 + 0.8 * backD * backD) * (1.0 - smoothstep(-2.7, -1.8, vWorld.z)) * (1.0 - smoothstep(-3.6, -3.0, vWorld.x)) * ao;

            // Blinn-Phong highlights of both lights, roughly normalised so that a tighter one is brighter, and the hall
            // reflected, both weighed by Schlick's Fresnel.
            vec3 f0 = mix(vec3(m.x), base, m.z);
            vec3 fresnel = f0 + (1.0 - f0) * pow(1.0 - max(dot(n, v), 0.0), 5.0) * m.w;
            float norm = (m.y + 8.0) / 25.0;
            // Brushed metal stretches its highlights along the brush, a box's face's first axis: aluminium most, steel
            // less, brass (redder than it is blue) a little.
            float aniso = vMaterial == 2u ? 0.85 : vMaterial == 3u ? (base.r > 1.8 * base.b ? 0.2 : 0.6) : 0.0;
            vec3 brush = vTangent - n * dot(vTangent, n);
            brush = dot(brush, brush) > 1e-8 ? normalize(brush) : vec3(1.0, 0.0, 0.0);
            vec3 highlight = keyColor * lobe(n, normalize(keyDir + v), m.y, brush, aniso) * key * shade
                + fillColor * lobe(n, normalize(fillDir + v), m.y, brush, aniso) * fill;
            vec3 lit = base * light + (highlight * norm + around(reflect(-v, n), vWorld) * ao) * fresnel;
            // A thin clear coat over oiled wood (Material.Wood, Material.Oak): its own sharp highlight and reflection,
            // off the face's smooth normal rather than the grain's, weighed by a dielectric's Fresnel.
            if (vMaterial == 7u || vMaterial == 18u)
            {
                float fc = 0.04 + 0.96 * pow(1.0 - max(dot(face, v), 0.0), 5.0);
                float coat = pow(max(dot(face, normalize(keyDir + v)), 0.0), 400.0) * key * shade * 16.0;
                lit = lit * (1.0 - fc) + (keyColor * coat + hall(reflect(-v, face)) * ao) * fc;
            }
            lit += vMaterial == 17u ? base * 0.5 : vec3(0.0); // a gallery picture's light from within
            // A city window mirrors the daylight sky (around, by Fresnel), and a little more of it seen face on.
            lit += vec3(0.42, 0.55, 0.75) * skyGlass * 0.06;
            lit = mix(lit, base, aerial); // a photograph already holds the day's light and shadows

            // A pool of light around the robot; the hall falls into the dark beyond it and above it. The knots' floor
            // has its own light over its tables (KnotFloor), its edge fading over 80 cm.
            float pool = 1.0 - smoothstep(0.8, 6.0, length(vWorld.xz - vec2(0.0, -0.3)));
            // A smaller pool under each pendant lamp over the hall's floor (HallShell.FloorLamps), below the lamps.
            vec2 lamps[4] = vec2[](vec2(-3.2, -0.6), vec2(0.0, 1.6), vec2(3.0, -0.6), vec2(-3.2, 3.4));
            for (int i = 0; i < 4; i++)
            {
                pool = max(pool, 0.7 * (1.0 - smoothstep(0.4, 3.4, length(vWorld.xz - lamps[i]))) * (1.0 - smoothstep(3.6, 4.4, vWorld.y)));
            }

            float knots = (1.0 - smoothstep(0.0, 0.8, box(vWorld.xz - vec2(5.075, -1.2), vec2(1.17, 2.1)))) * smoothstep(1.8, 2.0, vWorld.y);
            pool = max(pool, 0.9 * knots);
            // The gallery along the left wall is in its picture lights' light, up to their height (HallGallery).
            float gallery = (1.0 - smoothstep(0.0, 1.2, box(vWorld.xz - vec2(-5.7, 1.25), vec2(0.55, 4.65)))) * (1.0 - smoothstep(2.6, 3.4, vWorld.y));
            pool = max(pool, 0.8 * gallery);
            float catalogue = (1.0 - smoothstep(0.0, 1.0, box(vWorld.xz - vec2(-4.76, -3.1), vec2(1.2, 0.4)))) * (1.0 - smoothstep(2.6, 3.4, vWorld.y));
            pool = max(pool, 0.8 * catalogue);
            float high = max(1.0 - 0.6 * smoothstep(1.6, 4.5, vWorld.y), knots);
            // The streets beyond the front wall's roll-up door (OttawaStreets) are in daylight, every height of them.
            float street = city() ? 1.0 : smoothstep(6.0, 6.4, vWorld.z);
            pool = max(pool, street);
            high = max(high, street);
            // A video projected on the gallery's wall (GalleryVideo.Projection): a projector's light, 4:3, on what faces
            // the room within 6 cm of the wall, the wall's own colour showing through; its edges soft over 1 %.
            // The derivatives are taken outside the branch, where every pixel of a quad still runs.
            vec3 projected = vec3(0.0);
            vec2 extent = vec2(frame.screen.z, frame.screen.z * 0.75); // half sizes; "half" is reserved in GLSL
            vec2 onScreen = (frame.screen.xy + extent - vWorld.zy) / max(2.0 * extent, vec2(1e-4));
            vec2 screenDx = dFdx(onScreen), screenDy = dFdy(onScreen);
            if (frame.options.w > 0.0 && face.x > 0.5 && vWorld.x < frame.screen.w + 0.06)
            {
                vec2 inside = smoothstep(vec2(0.0), vec2(0.01), onScreen) * smoothstep(vec2(0.0), vec2(0.01), 1.0 - onScreen);
                if (inside.x * inside.y > 0.0)
                {
                    vec3 shown = textureGrad(pictureMaps, vec3(clamp(onScreen, 0.0, 1.0), frame.options.z), screenDx, screenDy).rgb;
                    projected = base * shown * 3.0 * face.x * frame.options.w * inside.x * inside.y;
                }
            }
            outColor = vec4(haze(shoulder(lit * (0.1 + 0.9 * pool) * high + projected) + pc.glow.rgb * pc.glow.a, vWorld), 1.0);
        }
        """;

    private const float Haze = 0.06f; // the haze's density per metre past its first 3 m: about half the light through at 15 m
    private static readonly float4 HazeColor = new(0.075f, 0.09f, 0.115f, 1f); // the shader's haze() colour
    private static readonly float4 DaySky = new(0.42f, 0.55f, 0.75f, 1f); // its day colour over the streets of Ottawa (OttawaStreets)

    // How the scene is lit (--quality): Flat as it was first drawn, or Low (LowFragmentShader).
    private enum Quality
    {
        Flat,
        Low,
    }

    // The splash: a textured rectangle with no vertex buffer, its six corners picked by the vertex's index.
    private const string SplashVertexShader = """
        #version 450
        layout(push_constant) uniform Push { vec4 rect; vec4 tint; } pc;
        layout(location = 0) out vec2 vUv;
        const vec2 corners[6] = vec2[](vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));
        void main()
        {
            vUv = corners[gl_VertexIndex];
            gl_Position = vec4(mix(pc.rect.xy, pc.rect.zw, vUv), 0.0, 1.0);
        }
        """;

    private const string SplashFragmentShader = """
        #version 450
        layout(set = 0, binding = 0) uniform sampler2D image;
        layout(push_constant) uniform Push { vec4 rect; vec4 tint; } pc;
        layout(location = 0) in vec2 vUv;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec4 texel = texture(image, vUv);
            outColor = vec4(texel.rgb * pc.tint.rgb, texel.a * pc.tint.a);
        }
        """;

    public static int Run(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            return Link.SelfTest();
        }

        // --question SESSION SURFACE: what the shelf would show for that waiting session, printed, nothing sent.
        if (Option(args, "--question") is { } questioned)
        {
            var asked = SessionQuestion.Ask(questioned, args.SkipWhile(a => a != "--question").Skip(2).FirstOrDefault() ?? "");
            Console.WriteLine($"QUESTION {asked.Title}: {asked.Text.ReplaceLineEndings(" ")[..Math.Min(100, asked.Text.Length)]}");
            foreach (var c in asked.Choices) Console.WriteLine($"QUESTION choice {c.Label} ({(c.WmuxChoice is { } reported ? "wmux " + reported : "key " + c.Key)})");
            if (asked.Note is { } note) Console.WriteLine($"QUESTION note {note}");
            return 0;
        }

        // --streets: the streets of Ottawa behind the door, fetched once (or read where they were kept) and built, without
        // a window: what the door would show, counted.
        if (args.Contains("--streets"))
        {
            var city = OttawaStreets.Load().GetAwaiter().GetResult();
            if (city is null) { Console.Error.WriteLine("STREETS no answer from Overpass"); return 1; }
            var p = city.Placement;
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"STREETS {city.Buildings} buildings, {city.Streets} streets, {city.Left} left out, {city.Vertices.Length / 3} triangles; door at ({p.Door.X:F1}, {p.Door.Y:F1}) m from Parliament Hill, out toward ({p.Out.X:F3}, {p.Out.Y:F3}); kept in {OttawaStreets.CachePath}"));
            return 0;
        }

        var clock = Stopwatch.StartNew();
        var limit = double.TryParse(Option(args, "--seconds"), out var seconds) ? seconds : double.PositiveInfinity;
        var demo = args.Contains("--demo");
        var moduleClickTest = args.Contains("--module-click-test");
        var hidden = args.Contains("--hidden");
        var vsync = !args.Contains("--immediate");
        var cap = int.TryParse(Option(args, "--cap"), out var capValue) ? capValue : hidden ? 60 : 0;
        var paceToScreen = false; // Mailbox without --cap: the cap follows the window's screen
        var port = int.TryParse(Option(args, "--listen"), out var portValue) ? portValue : 8805;
        var qualityAsked = Option(args, "--quality") ?? "auto";
        if (qualityAsked is not ("auto" or "flat" or "low"))
        {
            Console.Error.WriteLine($"SCENE refused: --quality takes auto, flat or low, not {qualityAsked}");
            return 2;
        }

        var peekDir = Option(args, "--peek"); // --peek DIR with --seconds: a PNG every 4 s of a plain run, the hall's own life (the tour, the robot)
        var shotsDir = Option(args, "--shots") ?? peekDir;
        // --film-arcade DIR: the arcade's film (ArcadeFilm), 45 s at 30 frames a second of the scene's own time, encoded
        // by FFmpeg into DIR\parsec-film.mp4, without the window's buttons; --hidden and --size make it apart from the
        // live window. Each frame is drawn and read back however long it takes.
        var filmDir = Option(args, "--film-arcade");
        // --film-origami DIR: the office's origami table playing its fold step by step (OrigamiFilm), filmed the same way
        // into DIR\origami-film.mp4, silent; --film-seconds S for another length than the crane's.
        var origamiDir = Option(args, "--film-origami");
        if (origamiDir is not null && filmDir is not null)
        {
            Console.Error.WriteLine("SCENE refused: one film at a time, --film-arcade or --film-origami");
            return 2;
        }

        var origamiFilmed = origamiDir is not null;
        filmDir ??= origamiDir;
        var origamiSeconds = float.TryParse(Option(args, "--film-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var filmSeconds) && filmSeconds > 0
            ? filmSeconds : OrigamiFilm.DefaultSeconds;
        const int FilmFps = 30;
        if (moduleClickTest && (!demo || shotsDir is null))
        {
            Console.Error.WriteLine("SCENE refused: --module-click-test requires --demo and --shots; never allowed on the live listener");
            return 2;
        }

        // --wall-test: what the wall screen shows, through the window's own clicks: a demo robot (the office's pick),
        // Delta's chest (its screen's hit test) and the cabinet (its hit test), each checked and shot. Demo and shots only.
        var wallTest = args.Contains("--wall-test");
        if (wallTest && (!demo || shotsDir is null))
        {
            Console.Error.WriteLine("SCENE refused: --wall-test requires --demo and --shots; never allowed on the live listener");
            return 2;
        }

        // --wall-real ID: the same wall on this computer's real sessions, read only: once the office has read them, the
        // session ID is picked as a click on its robot would (no command), and its card is printed and shot. Hidden, with
        // shots, off the live listener's port, never with --demo.
        var wallReal = Option(args, "--wall-real");
        if (wallReal is not null && (demo || shotsDir is null || !hidden || port == 8805))
        {
            Console.Error.WriteLine("SCENE refused: --wall-real requires --hidden, --shots and another --listen than 8805, without --demo");
            return 2;
        }
        // --walk-test: the views' keys and the walking keys, through the window's key handler and its camera loop: each of
        // the views 2, 3, 5 and 6 reached, then W and D held a second with the view turned down, which must walk on the
        // floor at eye height; then from the drift, W into Delta, which stops the body, and W and D past it. Demo, shots.
        var walkTest = args.Contains("--walk-test");
        if (walkTest && (!demo || shotsDir is null))
        {
            Console.Error.WriteLine("SCENE refused: --walk-test requires --demo and --shots");
            return 2;
        }
        var shelfAtStart = Option(args, "--shelf");
        Words.Speak(Option(args, "--lang") ?? "en");
        // Without their option, the Pong screen and the agents' office load from beside the scene when their image is
        // there, as Gaia's does; a refused default image is logged and the scene goes on without it.
        string? Installed(string name) => Path.Combine(AppContext.BaseDirectory, "modules", name) is var p && File.Exists(p) ? p : null;
        ScreenModuleSession? arcade = null;
        var arcadeOption = Option(args, "--arcade-module");
        if ((arcadeOption ?? Installed(Path.Combine("arcade", "ArcadePong.Module.dll"))) is { } arcadePath)
        {
            try { arcade = new ScreenModuleSession(arcadePath); }
            catch (Exception e)
            {
                Console.Error.WriteLine($"ARCADE module refused: {e.GetType().Name}: {e.Message}");
                if (arcadeOption is not null) return 2;
            }
        }
        var officePath = Option(args, "--office-module") ?? Installed(Path.Combine("office", "AgentOffice.Module.dll")); // the agents' office: a widget module, reloaded when its file changes
        const double S = Splash.Seconds;
        (double At, string Name)[] shots = shotsDir is null ? [] : [(1.3, "splash"), (S + 1.0, "idle"), (S + 2.6, "listening"), (S + 4.2, "thinking"), (S + 6.2, "speaking"), (S + 8.3, "full"), (S + 10.0, "compacting"), .. Link.Moods.Skip(1).Select((m, i) => (S + 11.8 + i, m)), (S + 19.6, "ignored"), (S + 20.5, "pairing")];
        if (moduleClickTest) shots = [(S + 1.0, "module-v1"), (S + 4.0, "module-v11")];
        if (wallTest) shots = [(S + 2.6, "wall-session"), (S + 4.6, "wall-pong"), (S + 6.6, "wall-parsec")];
        if (wallReal is not null) shots = [(S + 7.0, "wall-real")];
        if (walkTest) shots = [(S + 3.7, "walk-office-view"), (S + 4.9, "walk-office-walked"), (S + 21.2, "walk-delta")];
        if (peekDir is not null && double.IsFinite(limit)) shots = [.. Enumerable.Range(0, (int)Math.Max(0, (limit - S - 2.5) / 4)).Select(i => (S + 2 + (4.0 * i), $"peek-{i:D2}"))];
        var moduleTestClicked = false;
        var moduleTestCollected = false;
        var moduleTestGcChecked = false;
        var moduleTestSawSwap = false;
        if (shots.Length > 0 && !demo && peekDir is null && wallReal is null)
        {
            Console.Error.WriteLine("SCENE refused: --shots needs --demo, whose states come at known times");
            return 2;
        }

        var readsBack = shots.Length > 0 || filmDir is not null; // a frame's pixels come back from the GPU

        var logoPath = Path.Combine(AppContext.BaseDirectory, "brand", Splash.LogoFile);
        if (!File.Exists(logoPath))
        {
            Console.Error.WriteLine($"SCENE FAIL: {logoPath} is missing; BRUTAL's licence asks for its logo at start-up");
            return 1;
        }

        Console.WriteLine($"SCENE {Splash.Notice}");

        var link = new Link(() => clock.Elapsed.TotalSeconds);
        if (!demo)
        {
            var error = link.Listen(port);
            if (error is null)
            {
                Closed(port, false); // a window the brain started again, or that the user asked for
            }

            if (error is not null)
            {
                Console.Error.WriteLine($"SCENE FAIL: {error}");
                return 1;
            }

            Console.WriteLine($"SCENE listening on 127.0.0.1:{port}");
        }

        if (!Glfw.Init())
        {
            Console.Error.WriteLine("SCENE FAIL: Glfw.Init returned false");
            return 1;
        }

        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        Glfw.WindowHint(GlfwWindowHint.Resizable, 1);
        if (hidden)
        {
            Glfw.WindowHint(GlfwWindowHint.Visible, 0);
        }

        // A demo's window has its own title, so that the phone's gateway, which shows the one window titled Brickyard,
        // never takes it for Delta's (remote/window_windows.go).
        // --size WxH, for measuring a hidden window at a live one's size.
        var sized = Option(args, "--size")?.Split('x') is [var w, var h] && int.TryParse(w, out var sizeW) && int.TryParse(h, out var sizeH) && sizeW > 0 && sizeH > 0
            ? new int2(sizeW, sizeH)
            : new int2(Width, Height);
        var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = demo ? "Brickyard demo" : "Brickyard", Size = sized });
        fixed (byte* face = Tray.FaceRgba(64))
        {
            window.SetIcon(1, new GlfwImage { Width = 64, Height = 64, Pixels = face }); // Delta's face in the title bar and the taskbar
        }

        // Instance with the extensions GLFW needs to present, and the window's surface.
        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var extensionCount);
        var extensions = new HashSet<string>();
        for (var i = 0; i < extensionCount; i++)
        {
            extensions.Add(Marshal.PtrToStringUTF8((nint)glfwExtensions[i])!);
        }

        // NVIDIA Streamline (Streamline.cs) with --dlss: initialised before the instance and the device, whose extensions,
        // features and queues it adds to. --dlss-g alone implies --dlss. A window shown (not --hidden) takes it by
        // itself when NVIDIA's DLLs are in %LOCALAPPDATA%\delta\streamline, still each checked for NVIDIA's signature
        // before anything loads (the author's choice, 2026-11-15: "oui fais l'option 1 et mets en live"); --no-dlss
        // leaves it out.
        Streamline.Needs[] streamlineNeeds = [];
        var rayReconstruction = false; // DLSS-RR's functions bound once the device is told
        var superResolution = false; // DLSS's (Super Resolution) bound once the device is told
        var frameGeneration = false; // DLSS-G's, with the swapchain through Streamline (--dlss-g)
        var streamlineHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "streamline");
        var dlssByItself = !hidden && !args.Contains("--no-dlss") && File.Exists(Path.Combine(streamlineHome, "sl.interposer.dll"));
        if (args.Contains("--dlss") || args.Contains("--dlss-g") || dlssByItself)
        {
            var dir = Option(args, "--streamline") ?? streamlineHome;
            if (dlssByItself && !args.Contains("--dlss") && !args.Contains("--dlss-g"))
            {
                Console.WriteLine($"SCENE streamline: by itself, NVIDIA's DLLs found in {dir} (--no-dlss leaves it out)");
            }

            // Frame generation by itself too in a shown window when the folder also holds a file named frame-generation
            // (the author's word, 2026-11-15: "ajoute --dlss-g au lancement live", without restarting the brain that
            // launches the live window); --no-dlss-g leaves it out.
            var generationByItself = dlssByItself && !args.Contains("--no-dlss-g") && File.Exists(Path.Combine(dir, "frame-generation"));
            if (generationByItself && !args.Contains("--dlss-g"))
            {
                Console.WriteLine($"SCENE streamline: frame generation by itself, {Path.Combine(dir, "frame-generation")} found (--no-dlss-g leaves it out)");
            }

            if (Streamline.Init(dir, args.Contains("--dlss-g") || generationByItself ? Streamline.FrameGeneration : Streamline.Upscaling, args.Contains("--streamline-log")) is { } why)
            {
                Console.WriteLine($"SCENE streamline: not used ({why}) from {dir}");
            }
            else
            {
                streamlineNeeds = [.. Streamline.Features.Select(Streamline.Requirements).OfType<Streamline.Needs>()];
                foreach (var (feature, needs) in Streamline.Features.Zip(Streamline.Features.Select(Streamline.Requirements)))
                {
                    Console.WriteLine(needs is null
                        ? $"SCENE streamline {Streamline.FeatureName(feature)}: no requirements (not loaded)"
                        : $"SCENE streamline {Streamline.FeatureName(feature)}: instance [{string.Join(' ', needs.InstanceExtensions)}] device [{string.Join(' ', needs.DeviceExtensions)}] 1.2 [{string.Join(' ', needs.Features12)}] 1.3 [{string.Join(' ', needs.Features13)}] queues graphics {needs.GraphicsQueues} compute {needs.ComputeQueues} optical flow {needs.OpticalFlowQueues}");
                }

                foreach (var needs in streamlineNeeds)
                {
                    extensions.UnionWith(needs.InstanceExtensions);
                }
            }
        }

        var instance = Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = ApiVersion13 },
            EnabledExtensions = extensions,
            EnabledLayers = [],
        });
        if (Glfw.CreateWindowSurface(instance.VkHandle, window, out var rawSurface) != 0)
        {
            Console.Error.WriteLine("SCENE FAIL: no window surface");
            return 1;
        }

        var surface = new VkSurfaceKHR((nint)rawSurface);

        // The first discrete GPU with a queue family that draws and presents (any GPU if none is discrete).
        PhysicalDevice? physical = null;
        var family = -1;
        foreach (var pd in instance.PhysicalDevices.OrderBy(p => p.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu ? 0 : 1))
        {
            var count = pd.GetQueueFamilyProperties_Count();
            var families = new VkQueueFamilyProperties[count];
            pd.GetQueueFamilyProperties_Write(families);
            for (var i = 0; i < count && family < 0; i++)
            {
                if (families[i].QueueFlags.HasFlag(VkQueueFlags.GraphicsBit) && pd.GetSurfaceSupportKHR(i, surface))
                {
                    family = i;
                }
            }

            if (family >= 0)
            {
                physical = pd;
                break;
            }
        }

        if (physical is null)
        {
            Console.Error.WriteLine("SCENE FAIL: no GPU can draw and present to this window");
            return 1;
        }

        // The lighting: what --quality asks for, else Low on any GPU and Flat when the CPU draws, as a software
        // renderer does.
        var properties = physical.GetProperties();
        var gpu = Marshal.PtrToStringUTF8((nint)properties.DeviceName) ?? "this GPU";
        if (Streamline.Loaded)
        {
            foreach (var feature in Streamline.Features)
            {
                var supported = Streamline.Supported(feature, physical.VkHandle);
                Console.WriteLine($"SCENE streamline {Streamline.FeatureName(feature)} on {gpu}: {(supported == 0 ? "supported" : Streamline.ResultName(supported))}");
            }
        }
        var (quality, because) = qualityAsked switch
        {
            "flat" => (Quality.Flat, "--quality asks for it"),
            "low" => (Quality.Low, "--quality asks for it"),
            _ when properties.DeviceType == VkPhysicalDeviceType.Cpu => (Quality.Flat, $"{gpu} draws on the CPU"),
            _ => (Quality.Low, $"{gpu} is a {properties.DeviceType switch { VkPhysicalDeviceType.DiscreteGpu => "discrete", VkPhysicalDeviceType.IntegratedGpu => "integrated", VkPhysicalDeviceType.VirtualGpu => "virtual", _ => "other" }} GPU"),
        };
        // Low draws four samples a pixel, which every Vulkan GPU offers, and resolves them into the swapchain's image;
        // with DLSS Super Resolution offered (Streamline, an NVIDIA RTX GPU), one sample: DLSS smooths the edges instead,
        // from the jittered frames (RasterUpscaler), unless --no-dlss-sr.
        var dlssSmooths = quality == Quality.Low && Streamline.Loaded && !args.Contains("--no-dlss-sr")
            && Streamline.Features.Contains(Streamline.FeatureDlss) && Streamline.Supported(Streamline.FeatureDlss, physical.VkHandle) == 0;
        var samples = quality == Quality.Low && !dlssSmooths ? VkSampleCountFlags._4Bit : VkSampleCountFlags._1Bit;
        var msaa = samples != VkSampleCountFlags._1Bit;
        Console.WriteLine($"SCENE quality {quality.ToString().ToLowerInvariant()} because {because}{(msaa ? ", with 4x MSAA" : dlssSmooths ? ", its edges smoothed by DLSS instead of MSAA" : "")}");

        // The screen's pace (VK_KHR_present_id and VK_KHR_present_wait), when the GPU offers it: a frame can wait for the
        // last one to be on screen.
        var presentWaitFeature = new VkPhysicalDevicePresentWaitFeaturesKHR();
        var presentIdFeature = new VkPhysicalDevicePresentIdFeaturesKHR { Next = &presentWaitFeature };
        var canWait = DeviceExtensions(physical) is var offered && offered.Contains("VK_KHR_present_id") && offered.Contains("VK_KHR_present_wait");
        if (canWait)
        {
            var asked = new VkPhysicalDeviceFeatures2 { Next = &presentIdFeature };
            physical.GetFeatures2(ref asked);
            canWait = presentIdFeature.PresentId && presentWaitFeature.PresentWait;
        }

        // Anisotropic filtering, when offered, keeps the floor's and the wall's scans sharp where the view grazes them.
        var offeredFeatures = new VkPhysicalDeviceFeatures2();
        physical.GetFeatures2(ref offeredFeatures);
        bool anisotropic = offeredFeatures.Features.SamplerAnisotropy;
        var enabledFeatures = new VkPhysicalDeviceFeatures2 { Next = canWait ? &presentIdFeature : null };
        enabledFeatures.Features.SamplerAnisotropy = offeredFeatures.Features.SamplerAnisotropy;

        // Ray queries (RayScene), when the GPU offers them and the Low lighting is drawn: the key light's shadows and
        // the ambient occlusion traced through the hall instead of painted. --no-rays leaves them out.
        var rayTraced = quality == Quality.Low && !args.Contains("--no-rays") && RayScene.Offered(physical, DeviceExtensions(physical));
        RayScene.Features(out var rayV12, out var rayStructures, out var rayQueries);
        rayV12.BufferDeviceAddress = true;
        rayStructures.AccelerationStructure = true;
        rayQueries.RayQuery = true;
        // The ray tracing pipeline, when offered with the ray queries: the path tracer traces from a ray generation
        // shader. --pt-compute keeps it on the compute pass, to compare. NVIDIA's shader execution reordering (SER) is
        // there behind --ser only: sorting a bounce's rays by the instance they hit measured slower here (4K, view 7,
        // GPU p50: 38-40 ms against 26-34 ms without), the shading being one shader whatever is hit.
        var rtPipeline = rayTraced && !args.Contains("--pt-compute") && offered.Contains("VK_KHR_ray_tracing_pipeline");
        var reorder = rtPipeline && args.Contains("--ser") && offered.Contains("VK_NV_ray_tracing_invocation_reorder");
        var pipelineFeature = new VkPhysicalDeviceRayTracingPipelineFeaturesKHR();
        var reorderFeature = new VkPhysicalDeviceRayTracingInvocationReorderFeaturesNV();
        if (rtPipeline)
        {
            pipelineFeature.Next = reorder ? &reorderFeature : null;
            var asked = new VkPhysicalDeviceFeatures2 { Next = &pipelineFeature };
            physical.GetFeatures2(ref asked);
            rtPipeline = pipelineFeature.RayTracingPipeline;
            reorder &= rtPipeline && reorderFeature.RayTracingInvocationReorder;
            pipelineFeature = new VkPhysicalDeviceRayTracingPipelineFeaturesKHR { RayTracingPipeline = rtPipeline };
            reorderFeature = new VkPhysicalDeviceRayTracingInvocationReorderFeaturesNV { RayTracingInvocationReorder = reorder };
        }

        // NVIDIA Reflex (Reflex.cs) with --reflex, when offered with the present ids and timeline semaphores it needs.
        // --reflex-measure sets its markers and reads its times with the low latency mode off, to compare. Frame generation
        // (--dlss-g) runs Reflex through Streamline instead, on the swapchain Streamline owns.
        var reflexOn = (args.Contains("--reflex") || args.Contains("--reflex-measure")) && canWait && offered.Contains(Reflex.Extension)
            && !Streamline.Features.Contains(Streamline.FeatureDlssG);
        if (reflexOn)
        {
            var v12 = new VkPhysicalDeviceVulkan12Features();
            var asked = new VkPhysicalDeviceFeatures2 { Next = &v12 };
            physical.GetFeatures2(ref asked);
            reflexOn = v12.TimelineSemaphore;
            rayV12.TimelineSemaphore = reflexOn;
        }

        // Streamline's needs (--dlss), merged into the device: the extensions it names that the GPU offers, its Vulkan 1.2
        // and 1.3 features, and its queues after the scene's own one (sl_helpers_vk.h: "where first SL queue starts after
        // host's queues"). DLSS-G's optical flow takes the first queue of a family that does nothing else.
        var slExtensions = new List<string>();
        var sl12 = new HashSet<string>();
        var sl13 = new HashSet<string>();
        int slGraphics = 0, slCompute = 0, slFlow = 0;
        foreach (var needs in streamlineNeeds)
        {
            foreach (var e in needs.DeviceExtensions)
            {
                if (!offered.Contains(e))
                {
                    Console.WriteLine($"SCENE streamline: {gpu} does not offer {e}");
                }
                else if (!slExtensions.Contains(e))
                {
                    slExtensions.Add(e);
                }
            }

            sl12.UnionWith(needs.Features12);
            sl13.UnionWith(needs.Features13);
            slGraphics += needs.GraphicsQueues;
            slCompute += needs.ComputeQueues;
            slFlow += needs.OpticalFlowQueues;
        }

        var offered12 = new VkPhysicalDeviceVulkan12Features();
        var offered13 = new VkPhysicalDeviceVulkan13Features { Next = &offered12 };
        if (sl12.Count + sl13.Count > 0)
        {
            var asked = new VkPhysicalDeviceFeatures2 { Next = &offered13 };
            physical.GetFeatures2(ref asked);
        }

        if (!rayTraced && (reflexOn || sl12.Count > 0))
        {
            rayV12.BufferDeviceAddress = false;
            rayV12.Next = enabledFeatures.Next;
            enabledFeatures.Next = &rayV12;
        }

        foreach (var f in sl12)
        {
            bool on = false, known = true;
            switch (f)
            {
                case "timelineSemaphore": on = rayV12.TimelineSemaphore = offered12.TimelineSemaphore; break;
                case "descriptorIndexing": on = rayV12.DescriptorIndexing = offered12.DescriptorIndexing; break;
                case "bufferDeviceAddress": on = rayV12.BufferDeviceAddress = offered12.BufferDeviceAddress; break;
                default: known = false; break;
            }

            if (!on)
            {
                Console.WriteLine($"SCENE streamline: Vulkan 1.2 {f} {(known ? "not offered" : "not known to the scene")}");
            }
        }

        var v13 = new VkPhysicalDeviceVulkan13Features();
        foreach (var f in sl13)
        {
            bool on = false, known = f == "synchronization2";
            if (known)
            {
                on = v13.Synchronization2 = offered13.Synchronization2;
            }

            if (!on)
            {
                Console.WriteLine($"SCENE streamline: Vulkan 1.3 {f} {(known ? "not offered" : "not known to the scene")}");
            }
        }

        // The queue families Streamline's queues come from: a compute family that does not draw, else the scene's own;
        // an optical flow family apart from both.
        var familyFlags = new VkQueueFamilyProperties[physical.GetQueueFamilyProperties_Count()];
        physical.GetQueueFamilyProperties_Write(familyFlags);
        const VkQueueFlags OpticalFlowBit = (VkQueueFlags)0x100;
        var computeFamily = Array.FindIndex(familyFlags, f => f.QueueFlags.HasFlag(VkQueueFlags.ComputeBit) && !f.QueueFlags.HasFlag(VkQueueFlags.GraphicsBit));
        if (computeFamily < 0 || slCompute == 0)
        {
            computeFamily = family;
        }

        var flowFamily = slFlow == 0 ? -1 : Array.FindIndex(familyFlags, f => f.QueueFlags.HasFlag(OpticalFlowBit));
        if (flowFamily == family || flowFamily == computeFamily)
        {
            flowFamily = -1;
        }

        var graphicsCount = (int)Math.Min(1 + slGraphics + (computeFamily == family ? slCompute : 0), familyFlags[family].QueueCount);
        var computeCount = computeFamily == family ? 0 : (int)Math.Min(slCompute, familyFlags[computeFamily].QueueCount);
        if (graphicsCount < 1 + slGraphics + (computeFamily == family ? slCompute : 0) || computeCount < (computeFamily == family ? 0 : slCompute))
        {
            Console.WriteLine($"SCENE streamline: fewer queues than asked (graphics {graphicsCount}, compute {computeCount})");
        }

        if (slFlow > 0 && flowFamily < 0)
        {
            Console.WriteLine("SCENE streamline: no optical flow queue family apart; DLSS-G would run its optical flow in interop");
        }

        var priorities = stackalloc float[32];
        for (var i = 0; i < 32; i++)
        {
            priorities[i] = 1.0f;
        }

        var queueInfos = stackalloc VkDeviceQueueCreateInfo[3];
        var queueInfoCount = 0;
        queueInfos[queueInfoCount++] = new VkDeviceQueueCreateInfo { QueueFamilyIndex = family, QueueCount = Math.Min(graphicsCount, 32), QueuePriorities = priorities };
        if (computeCount > 0)
        {
            queueInfos[queueInfoCount++] = new VkDeviceQueueCreateInfo { QueueFamilyIndex = computeFamily, QueueCount = Math.Min(computeCount, 32), QueuePriorities = priorities };
        }

        if (flowFamily >= 0)
        {
            queueInfos[queueInfoCount++] = new VkDeviceQueueCreateInfo { QueueFamilyIndex = flowFamily, QueueCount = 1, QueuePriorities = priorities };
        }

        if (rayTraced)
        {
            rayQueries.Next = enabledFeatures.Next;
            if (rtPipeline)
            {
                reorderFeature.Next = rayQueries.Next;
                pipelineFeature.Next = reorder ? &reorderFeature : rayQueries.Next;
                rayQueries.Next = &pipelineFeature;
            }

            rayStructures.Next = &rayQueries;
            rayV12.Next = &rayStructures;
            enabledFeatures.Next = &rayV12;
        }

        Mesh.RayTraced = rayTraced;
        string[] deviceExtensions = canWait ? ["VK_KHR_swapchain", "VK_KHR_present_id", "VK_KHR_present_wait"] : ["VK_KHR_swapchain"];
        if (reflexOn)
        {
            deviceExtensions = [.. deviceExtensions, Reflex.Extension];
        }

        string[] pipelineExtensions = rtPipeline ? reorder ? ["VK_KHR_ray_tracing_pipeline", "VK_NV_ray_tracing_invocation_reorder"] : ["VK_KHR_ray_tracing_pipeline"] : [];
        if (sl13.Count > 0)
        {
            v13.Next = enabledFeatures.Next;
            enabledFeatures.Next = &v13;
        }

        string[] ownExtensions = rayTraced ? [.. deviceExtensions, .. RayScene.Extensions, .. pipelineExtensions] : [.. deviceExtensions];
        var device = physical.CreateDevice(new Device.CreateInfo
        {
            Extensions = [.. ownExtensions, .. slExtensions.Where(e => !ownExtensions.Contains(e))],
            QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(queueInfos, queueInfoCount),
            Next = &enabledFeatures,
        });
        var queue = device.GetQueue(family, 0);
        if (Streamline.Loaded)
        {
            var told = Streamline.SetVulkanInfo(device.VkHandle, instance.VkHandle, physical.VkHandle, (uint)family, 1,
                (uint)computeFamily, computeFamily == family ? (uint)(1 + slGraphics) : 0, flowFamily < 0 ? 0 : (uint)flowFamily, 0, flowFamily >= 0);
            Console.WriteLine($"SCENE streamline told of the device: {Streamline.ResultName(told)}; queues graphics {family}x{graphicsCount} compute {computeFamily}x{(computeFamily == family ? slCompute : computeCount)} optical flow {(flowFamily < 0 ? "none" : $"{flowFamily}x1")}");
            if (told == 0)
            {
                Streamline.BindPresent(device.VkHandle);
            }

            if (told == 0 && Streamline.Supported(Streamline.FeatureDlssRR, physical.VkHandle) == 0)
            {
                var bound = Streamline.BindRayReconstruction();
                rayReconstruction = bound is null;
                Console.WriteLine($"SCENE streamline ray reconstruction: {bound ?? "ready"}");
            }

            // Super Resolution on the raster pass (RasterUpscaler), unless --no-dlss-sr; not with Low's MSAA.
            if (told == 0 && !msaa && !args.Contains("--no-dlss-sr") && Streamline.Supported(Streamline.FeatureDlss, physical.VkHandle) == 0)
            {
                var bound = Streamline.BindSuperResolution();
                superResolution = bound is null;
                Console.WriteLine($"SCENE streamline super resolution: {bound ?? "ready"}");
            }

            // Frame generation (--dlss-g): its functions, the swapchain's proxies and Reflex's low latency mode, then
            // the window's surface made again through the interposer, so that DLSS-G knows it.
            if (told == 0 && rayReconstruction && Streamline.Features.Contains(Streamline.FeatureDlssG))
            {
                var supported = Streamline.Supported(Streamline.FeatureDlssG, physical.VkHandle);
                var bound = supported != 0 ? Streamline.ResultName(supported) : Streamline.BindFrameGeneration(device.VkHandle);
                if (bound is null)
                {
                    instance.DestroySurfaceKHR(surface, null);
                    var (made, why) = Streamline.CreateSurface(instance.VkHandle, ModalDrill.Own());
                    if (why is not null)
                    {
                        Console.Error.WriteLine($"SCENE FAIL: the window's surface through Streamline: {why}");
                        return 1;
                    }

                    surface = new VkSurfaceKHR(made);
                    frameGeneration = true;
                    if (uint.TryParse(Option(args, "--dlss-g-frames"), out var generated))
                    {
                        Streamline.FramesToGenerate = generated;
                    }
                }

                Console.WriteLine($"SCENE streamline frame generation: {bound ?? $"ready, {Streamline.FramesToGenerate + 1}x the frames drawn, Reflex through Streamline"}");
            }
        }
        var reflex = reflexOn ? new Reflex(device, args.Contains("--reflex")) : null;

        // What the GPU offers of NVIDIA's RTX, for the F10 card: the extensions it names, and its driver as NVIDIA
        // numbers it (10 bits major, 8 minor) or as Vulkan does.
        var rtxOffered = DeviceExtensions(physical);
        var nvidia = properties.VendorID == 0x10DE;
        var driverVersion = unchecked((uint)properties.DriverVersion);
        var driver = nvidia
            ? $"{Words.Say("driver", "pilote")} {driverVersion >> 22}.{(driverVersion >> 14) & 0xFF:D2}"
            : $"{Words.Say("driver", "pilote")} {driverVersion >> 22}.{(driverVersion >> 12) & 0x3FF}";
        bool Has(string extension) => rtxOffered.Contains(extension);

        // Swapchain: sRGB BGRA if offered; vsync unless --immediate.
        var formatCount = physical.GetSurfaceFormatsKHR_Count(surface);
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[formatCount];
        physical.GetSurfaceFormatsKHR_Write(surface, formats);
        var surfaceFormat = formats[0];
        foreach (var f in formats)
        {
            if (f.Format == VkFormat.B8G8R8A8SRGB && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
            {
                surfaceFormat = f;
            }
        }

        var modeCount = physical.GetSurfacePresentModesKHR_Count(surface);
        Span<VkPresentModeKHR> modes = stackalloc VkPresentModeKHR[modeCount];
        physical.GetSurfacePresentModesKHR_Write(surface, modes);
        var presentMode = VkPresentModeKHR.FifoKHR;
        // Not with frame generation: Streamline presents its frames and those it makes from a queue of its own.
        var paceByScreen = vsync && canWait && !frameGeneration && reflex is not { LowLatency: true } &&!args.Contains("--fifo") && !args.Contains("--mailbox") && (!hidden || args.Contains("--present-wait"));
        if (!vsync)
        {
            foreach (var wanted in new[] { VkPresentModeKHR.ImmediateKHR, VkPresentModeKHR.MailboxKHR })
            {
                if (presentMode == VkPresentModeKHR.FifoKHR && modes.Contains(wanted))
                {
                    presentMode = wanted;
                }
            }
        }
        else if (paceByScreen)
        {
            // Shown, FIFO at the screen's own pace: each frame waits for the last one to be on screen before it reads the
            // keys and the mouse, so a move shows at the next refresh and every refresh shows a new picture, an even step.
            // Mailbox below drew at twice the refresh against the screen's clock, and the picture it showed was by turns
            // fresh and 8 ms old: the user saw it jerk. --present-wait does it hidden too, for measuring.
        }
        else if (frameGeneration)
        {
            // Frame generation: FIFO, at the screen's pace, which Streamline keeps with the frames it presents (drawn and
            // generated); a mailbox at twice the refresh would throw away the frames it makes.
        }
        else if (!hidden && reflex is not { LowLatency: true } && !args.Contains("--fifo") && modes.Contains(VkPresentModeKHR.MailboxKHR))
        {
            // Shown, Mailbox when offered: no tearing, and the screen takes the newest picture instead of FIFO's queue
            // of them, so a move shows a refresh or two sooner; paced at twice the refresh of the screen the window is
            // on (the loop looks again every 2 s, as the window is placed or moved): at the refresh itself the pacing
            // drifts against the screen's and now and then a picture shows twice. --fifo keeps FIFO, --mailbox takes this.
            presentMode = VkPresentModeKHR.MailboxKHR;
            if (Option(args, "--cap") is null)
            {
                paceToScreen = true;
                cap = 120;
            }
        }

        if (reflex is { LowLatency: true })
        {
            // Reflex paces to the cap itself (Reflex.IntervalUs); the scene's own sleep is left out below.
            reflex.IntervalUs = cap > 0 ? 1_000_000 / cap : 0;
        }

        // The swapchain and what is as large as the window, built by Build and rebuilt when the window changes size.
        const VkFormat depthFormat = VkFormat.D32SFloat;
        var extent = default(VkExtent2D);
        var swapchain = default(VkSwapchainKHR);
        var hasSwapchain = false;
        var images = Array.Empty<VkImage>();
        var depthImage = default(VkImage);
        var depthMemory = default(VkDeviceMemory);
        var depthView = default(VkImageView);
        var msaaImage = default(VkImage);
        var msaaMemory = default(VkDeviceMemory);
        var msaaView = default(VkImageView);
        var views = Array.Empty<VkImageView>();
        var framebuffers = Array.Empty<VkFramebuffer>();
        var renderDone = Array.Empty<VkSemaphore>();
        var streamlineImages = Array.Empty<VkImage>(); // frame generation: Streamline's back buffers; images are then the canvases
        var canvasMemory = Array.Empty<VkDeviceMemory>();

        // One subpass: colour to present, depth cleared and discarded. With MSAA, the colour's samples are discarded too
        // once resolved into the swapchain's image, the third attachment.
        Span<VkAttachmentDescription> attachments =
        [
            new()
            {
                Format = surfaceFormat.Format,
                Samples = samples,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = msaa ? VkAttachmentStoreOp.DontCare : VkAttachmentStoreOp.Store,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.Undefined,
                FinalLayout = msaa ? VkImageLayout.ColorAttachmentOptimal : VkImageLayout.PresentSrcKHR,
            },
            new()
            {
                Format = depthFormat,
                Samples = samples,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.DontCare,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.Undefined,
                FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            },
            new()
            {
                Format = surfaceFormat.Format,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = VkAttachmentLoadOp.DontCare,
                StoreOp = VkAttachmentStoreOp.Store,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.Undefined,
                FinalLayout = VkImageLayout.PresentSrcKHR,
            },
        ];
        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var depthRef = new VkAttachmentReference { Attachment = 1, Layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var resolveRef = new VkAttachmentReference { Attachment = 2, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorRef,
            ResolveAttachments = msaa ? &resolveRef : null,
            DepthStencilAttachment = &depthRef,
        };
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.LateFragmentTestsBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
        };
        var renderPass = device.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = msaa ? 3 : 2,
            Attachments = (VkAttachmentDescription*)Unsafe.AsPointer(ref attachments[0]),
            SubpassCount = 1,
            Subpasses = &subpass,
            DependencyCount = 1,
            Dependencies = &dependency,
        }, null);

        // Frame generation draws a frame in two passes, compatible with renderPass and its framebuffers: the hall
        // (hallPass), which leaves the canvas as a colour attachment so that Streamline can copy it as the HUD-less colour,
        // then the cards and buttons over it (cardsPass), which ends as renderPass does. With MSAA the samples are kept
        // between the two, the hall's pass resolves them into the canvas, and the cards' pass loads them and resolves
        // them again, cards and all.
        var hudless = frameGeneration;
        var hallPass = default(VkRenderPass);
        var cardsPass = default(VkRenderPass);
        // Super Resolution (RasterUpscaler) draws the hall into its own pass, then the cards' pass over the window's
        // image: DLSS's picture first, the cards after it.
        if (hudless || superResolution)
        {
            Span<VkAttachmentDescription> split = [attachments[0], attachments[1], attachments[2]];
            if (msaa)
            {
                split[0].StoreOp = VkAttachmentStoreOp.Store;
                split[2].FinalLayout = VkImageLayout.ColorAttachmentOptimal;
            }
            else
            {
                split[0].FinalLayout = VkImageLayout.ColorAttachmentOptimal;
            }

            hallPass = device.CreateRenderPass(new VkRenderPassCreateInfo
            {
                AttachmentCount = msaa ? 3 : 2,
                Attachments = (VkAttachmentDescription*)Unsafe.AsPointer(ref split[0]),
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 1,
                Dependencies = &dependency,
            }, null);
            split[0].LoadOp = VkAttachmentLoadOp.Load;
            split[0].InitialLayout = VkImageLayout.ColorAttachmentOptimal;
            if (msaa)
            {
                split[0].StoreOp = VkAttachmentStoreOp.DontCare;
                split[2].FinalLayout = VkImageLayout.PresentSrcKHR;
            }
            else
            {
                split[0].FinalLayout = VkImageLayout.PresentSrcKHR;
            }

            split[1].LoadOp = VkAttachmentLoadOp.DontCare; // the cards test no depth
            cardsPass = device.CreateRenderPass(new VkRenderPassCreateInfo
            {
                AttachmentCount = msaa ? 3 : 2,
                Attachments = (VkAttachmentDescription*)Unsafe.AsPointer(ref split[0]),
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 1,
                Dependencies = &dependency,
            }, null);
        }

        // Builds the swapchain at the window's size, with its depth buffer, image views, framebuffers and semaphores. It
        // is false while the window has no size, when it is minimised.
        bool Build()
        {
            var caps = physical.GetSurfaceCapabilitiesKHR(surface);
            var size = caps.CurrentExtent.Width == -1 ? new VkExtent2D { Width = window.FramebufferSize.X, Height = window.FramebufferSize.Y } : caps.CurrentExtent;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return false;
            }

            extent = size;
            var imageCount = Math.Max(caps.MinImageCount, 3);
            if (caps.MaxImageCount != 0)
            {
                imageCount = Math.Min(imageCount, caps.MaxImageCount);
            }

            var old = swapchain;
            var latency = new VkSwapchainLatencyCreateInfoNV { LatencyModeEnable = true };
            var swapchainInfo = new VkSwapchainCreateInfoKHR
            {
                Next = reflex is null ? null : &latency,
                Surface = surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit | (readsBack ? VkImageUsageFlags.TransferSrcBit : default),
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = caps.CurrentTransform,
                CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
                PresentMode = presentMode,
                Clipped = true,
                OldSwapchain = old,
            };
            if (frameGeneration)
            {
                // Through Streamline's proxies: the images it hands back are its own, drawn off screen and presented
                // by it with the frames it generates between them.
                // DLSS-G refuses sRGB back buffers: Streamline's are the same bytes as UNORM, and the scene draws into
                // sRGB images of its own (canvases), copied into Streamline's at the frame's end, bit for bit.
                swapchainInfo.ImageFormat = surfaceFormat.Format switch
                {
                    VkFormat.B8G8R8A8SRGB => VkFormat.B8G8R8A8UNorm,
                    VkFormat.R8G8B8A8SRGB => VkFormat.R8G8B8A8UNorm,
                    var f => f,
                };
                swapchainInfo.ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit;
                var made = Streamline.CreateSwapchain(&swapchainInfo, out var proxied);
                if (made != 0)
                {
                    throw new InvalidOperationException($"vkCreateSwapchainKHR through Streamline: {(VkResult)made}");
                }

                swapchain = new VkSwapchainKHR(proxied);
                if (hasSwapchain)
                {
                    Streamline.DestroySwapchain(old.VkHandle);
                }

                streamlineImages = [.. Streamline.SwapchainImages(swapchain.VkHandle).Select(i => new VkImage(i))];
                images = new VkImage[streamlineImages.Length];
                canvasMemory = new VkDeviceMemory[streamlineImages.Length];
                for (var i = 0; i < images.Length; i++)
                {
                    images[i] = device.CreateImage(new VkImageCreateInfo
                    {
                        ImageType = VkImageType._2D,
                        Format = surfaceFormat.Format,
                        Extent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
                        MipLevels = 1,
                        ArrayLayers = 1,
                        Samples = VkSampleCountFlags._1Bit,
                        Tiling = VkImageTiling.Optimal,
                        Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
                        SharingMode = VkSharingMode.Exclusive,
                        InitialLayout = VkImageLayout.Undefined,
                    }, null);
                    canvasMemory[i] = device.AllocateMemory(physical, images[i], VkMemoryPropertyFlags.DeviceLocalBit, null);
                    device.BindImageMemory(images[i], canvasMemory[i], ByteSize.Zero);
                }
            }
            else
            {
                swapchain = device.CreateSwapchainKHR(swapchainInfo, null);
                if (hasSwapchain)
                {
                    device.DestroySwapchainKHR(old, null);
                }

                images = new VkImage[device.GetSwapchainImagesKHR_Count(swapchain)];
                device.GetSwapchainImagesKHR_Write(swapchain, images);
            }

            hasSwapchain = true;
            reflex?.Mode(swapchain);

            depthImage = device.CreateImage(new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = depthFormat,
                Extent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = samples,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.DepthStencilAttachmentBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            }, null);
            depthMemory = device.AllocateMemory(physical, depthImage, VkMemoryPropertyFlags.DeviceLocalBit, null);
            device.BindImageMemory(depthImage, depthMemory, ByteSize.Zero);
            depthView = device.CreateImageView(View(depthImage, depthFormat, VkImageAspectFlags.DepthBit), null);
            if (msaa)
            {
                msaaImage = device.CreateImage(new VkImageCreateInfo
                {
                    ImageType = VkImageType._2D,
                    Format = surfaceFormat.Format,
                    Extent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = samples,
                    Tiling = VkImageTiling.Optimal,
                    // Frame generation keeps the samples from the hall's pass to the cards': not transient then.
                    Usage = VkImageUsageFlags.ColorAttachmentBit | (hudless ? default : VkImageUsageFlags.TransientAttachmentBit),
                    SharingMode = VkSharingMode.Exclusive,
                    InitialLayout = VkImageLayout.Undefined,
                }, null);
                msaaMemory = device.AllocateMemory(physical, msaaImage, VkMemoryPropertyFlags.DeviceLocalBit, null);
                device.BindImageMemory(msaaImage, msaaMemory, ByteSize.Zero);
                msaaView = device.CreateImageView(View(msaaImage, surfaceFormat.Format, VkImageAspectFlags.ColorBit), null);
            }

            views = new VkImageView[images.Length];
            framebuffers = new VkFramebuffer[images.Length];
            renderDone = new VkSemaphore[images.Length];
            var targets = stackalloc VkImageView[] { msaaView, depthView, default };
            for (var i = 0; i < images.Length; i++)
            {
                views[i] = device.CreateImageView(View(images[i], surfaceFormat.Format, VkImageAspectFlags.ColorBit), null);
                targets[msaa ? 2 : 0] = views[i];
                framebuffers[i] = device.CreateFramebuffer(new VkFramebufferCreateInfo
                {
                    RenderPass = renderPass,
                    AttachmentCount = msaa ? 3 : 2,
                    Attachments = targets,
                    Width = extent.Width,
                    Height = extent.Height,
                    Layers = 1,
                }, null);
                renderDone[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            }

            return true;
        }

        // Destroys what Build made, but the swapchain, which the next one replaces.
        void Teardown()
        {
            for (var i = 0; i < images.Length; i++)
            {
                device.DestroySemaphore(renderDone[i], null);
                device.DestroyFramebuffer(framebuffers[i], null);
                device.DestroyImageView(views[i], null);
            }

            device.DestroyImageView(depthView, null);
            device.DestroyImage(depthImage, null);
            device.FreeMemory(depthMemory, null);
            if (msaa)
            {
                device.DestroyImageView(msaaView, null);
                device.DestroyImage(msaaImage, null);
                device.FreeMemory(msaaMemory, null);
            }

            for (var i = 0; i < canvasMemory.Length; i++)
            {
                device.DestroyImage(images[i], null);
                device.FreeMemory(canvasMemory[i], null);
            }

            canvasMemory = [];
            streamlineImages = [];
            images = [];
        }

        if (!Build())
        {
            Console.Error.WriteLine("SCENE FAIL: the window has no size");
            return 1;
        }

        // Shaders, compiled from GLSL at start-up.
        var compileClock = Stopwatch.StartNew();
        var vertexModule = Shader(device, VertexShader, ShaderKind.VertexShader, "scene.vert");
        var fragmentModule = quality == Quality.Low
            ? Shader(device, rayTraced ? LowFragmentShader.Replace("#version 450", "#version 460\n#extension GL_EXT_ray_query : require\n#define RAYS 1") : LowFragmentShader,
                ShaderKind.FragmentShader, rayTraced ? "low-rays.frag" : "low.frag", vulkan13: rayTraced)
            : Shader(device, FragmentShader, ShaderKind.FragmentShader, "scene.frag");
        Console.WriteLine($"SCENE shaders compiled in {compileClock.Elapsed.TotalMilliseconds:F0} ms");

        var pushRange = new VkPushConstantRange { StageFlags = PushStages, Offset = ByteSize.Zero, Size = ByteSize.Of<Push>() };
        // The Low shader's Frame: a uniform buffer per frame in flight, in one host-visible buffer at 256-byte steps,
        // the largest offset alignment Vulkan allows.
        const int frameStep = 256;
        // Bindings 1 to 3: the photographed materials' colour, normal and ARM images (MaterialTextures), written once loaded.
        var frameLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
        {
            Bindings =
            [
                new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
                new VkDescriptorSetLayoutBinding { Binding = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
                new VkDescriptorSetLayoutBinding { Binding = 2, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
                new VkDescriptorSetLayoutBinding { Binding = 3, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
                new VkDescriptorSetLayoutBinding { Binding = 5, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
                new VkDescriptorSetLayoutBinding { Binding = 6, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
                new VkDescriptorSetLayoutBinding { Binding = 7, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
                .. (rayTraced
                    ? new[] { new VkDescriptorSetLayoutBinding { Binding = 4, DescriptorType = VkDescriptorType.AccelerationStructureKHR, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit } }
                    : []),
            ],
        }, null);
        var framePool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
        {
            MaxSets = FramesInFlight,
            PoolSizes =
            [
                new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = FramesInFlight },
                new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 6 * FramesInFlight },
                .. (rayTraced ? new[] { new VkDescriptorPoolSize { Type = VkDescriptorType.AccelerationStructureKHR, DescriptorCount = FramesInFlight } } : []),
            ],
        }, null);
        var frameSize = new ByteSize((nuint)(frameStep * FramesInFlight));
        var frameBuffer = device.CreateBuffer(new VkBufferCreateInfo { Size = frameSize, Usage = VkBufferUsageFlags.UniformBufferBit, SharingMode = VkSharingMode.Exclusive }, null);
        var frameMemory = device.AllocateMemory(physical, frameBuffer, VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, null);
        device.BindBufferMemory(frameBuffer, frameMemory, memoryOffset: ByteSize.Zero);
        var frameMapped = (byte*)device.MapMemory<byte>(new MapInfo(frameMemory) { Region = new MapRegion { Size = frameSize } });
        var frameSets = new VkDescriptorSet[FramesInFlight];
        for (var i = 0; i < FramesInFlight; i++)
        {
            frameSets[i] = device.AllocateDescriptorSet(framePool, frameLayout);
            var bufferInfo = new VkDescriptorBufferInfo { Buffer = frameBuffer, Offset = new ByteSize64((ulong)(i * frameStep)), Range = ByteSize.Of<Frame>() };
            device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = frameSets[i], DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &bufferInfo }], []);
        }

        // Each frame's top-level structure (RayScene), bound once: rebuilt in place each frame, the handle stays.
        var rays = rayTraced ? new RayScene(device, physical, FramesInFlight) : null;
        Rays = rays;
        for (var i = 0; i < FramesInFlight && rays is not null; i++)
        {
            var top = rays.Top(i);
            var structureInfo = new VkWriteDescriptorSetAccelerationStructureKHR { AccelerationStructureCount = 1, AccelerationStructures = &top };
            device.UpdateDescriptorSets([new VkWriteDescriptorSet { Next = &structureInfo, DstSet = frameSets[i], DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.AccelerationStructureKHR }], []);
        }

        Console.WriteLine(rays is not null ? "SCENE rays: shadows and ambient occlusion traced (VK_KHR_ray_query); R toggles them"
            : $"SCENE rays: none ({(quality != Quality.Low ? "the Flat lighting" : args.Contains("--no-rays") ? "--no-rays" : "the GPU offers no ray queries")})");
        var layout = device.CreatePipelineLayout([frameLayout], [pushRange], null);
        var pipeline = Pipeline(device, layout, renderPass, samples, vertexModule, fragmentModule);

        // The hall, still, and the robot's parts, each moved by its own matrix. With the photographed materials there
        // (MaterialTextures), the wall is one surface carrying the scan of real brick, its relief drawn by the shader.
        var materialDir = quality == Quality.Low && !args.Contains("--no-textures") ? MaterialTextures.Find(Option(args, "--textures")) : null;
        var hallVertices = Brickwork.Build(photographed: materialDir is not null);
        var hall = new Mesh(device, physical, [.. hallVertices, .. KnotFloor.Build(photographed: materialDir is not null)]);
        // The hall closed round (HallShell): walls, ceiling, windows and lamps, drawn but not traced.
        var (shellVertices, glassVertices, bulbVertices) = HallShell.Build(photographed: materialDir is not null);
        var (shell, shellGlass, shellBulbs) = (new Mesh(device, physical, shellVertices), new Mesh(device, physical, glassVertices), new Mesh(device, physical, bulbVertices));
        // The roll-up door's curtain, and the streets of Ottawa behind it once fetched (StreetDoor, OttawaStreets).
        var streetDoor = new StreetDoor();
        var doorCurtain = new Mesh(device, physical, StreetDoor.Curtain());
        Task<OttawaStreets.City?>? streetsLoading = null;
        Mesh? streets = null;
        OttawaStreets.Placement? streetsPlacement = null;
        IReadOnlyList<FreeCamera.Footprint> streetFootprints = [];
        // Google's 3D tiles in place of OpenStreetMap's buildings (G, or --google-tiles; GoogleStreets): fetched once a
        // run, at most GoogleStreets.MostRequests requests, kept in memory only. While they are chosen nothing of
        // OpenStreetMap is drawn beside them (Google's terms, 3.2.3 (e)) and the buildings do not stop the walker.
        var googleChosen = args.Contains("--google-tiles");
        var googleTried = false;
        Task<GoogleStreets.Tiles?>? googleLoading = null;
        Mesh? aerial = null;
        IReadOnlyList<string> aerialProviders = [];
        // The gallery on the left wall (HallGallery): the learn site's courses, demos and artifacts, from gallery.json
        // (--gallery FILE), drawn from Lego's blocks and redrawn when the file changes; a click on one opens its page.
        // What the demos and artifacts show inside their glasses (GalleryPictures, --pictures DIR), read first.
        var picturesDir = quality == Quality.Low && !args.Contains("--no-textures") ? GalleryPictures.Find(Option(args, "--pictures")) : null;
        GalleryPictures.Pixels? picturePixels = GalleryPictures.Read(picturesDir);
        HallGallery.Pictures = picturePixels.Held;
        Console.WriteLine($"SCENE gallery pictures: {picturePixels.Held.Count}{(picturesDir is null ? " (the Flat lighting or --no-textures)" : $" from {picturesDir}")}");
        var hallGallery = new HallGallery(vertices => new Mesh(device, physical, vertices), FramesInFlight, Option(args, "--gallery"));
        Console.WriteLine($"SCENE gallery: {hallGallery.Summary}");
        // The knots' floor's gallery, as brain -ix-knots wrote it from IX's readings (--knots FILE), in each language.
        var knotsPath = KnotFloor.Path(Option(args, "--marins"));
        var marins = KnotFloor.Load(knotsPath);
        var (ropeVertices, cardsEn, cardsFr) = (KnotFloor.BuildKnots(marins), KnotFloor.BuildCards(marins, fr: false), KnotFloor.BuildCards(marins, fr: true));
        var knotRopes = new Mesh(device, physical, ropeVertices, resident: true);
        // Where the ropes and the cards are, to draw them only when the picture shows some of it (KnotFloor.Seen).
        var knotBox = KnotFloor.Box(ropeVertices, cardsEn, cardsFr);
        var knots = new Dictionary<bool, Mesh>
        {
            [false] = new Mesh(device, physical, cardsEn),
            [true] = new Mesh(device, physical, cardsFr),
        };
        (ropeVertices, cardsEn, cardsFr) = ([], [], []);
        Console.WriteLine(marins.Knots.Length > 0
            ? $"SCENE knots: {marins.Knots.Length} sailors' knots from {knotsPath}, {marins.Knots.Count(k => k.Verified)} verified by IX, {knotRopes.Count} vertices of rope{(knotRopes.Resident ? " in the GPU's memory" : " in the host's memory")}, the fuller table {KnotFloor.Fill:P0} full; "
              + $"each knot moved from IX's path at most, its clearance, tightest turn, steepest slope and plan crossings/writhe, IX's then as shown: {string.Join(", ", marins.Knots.Select(k => (k, m: KnotFloor.Measure(k))).Select(x => $"{x.k.Id} {x.m.Moved:F1} r, {x.m.Before:F2}>{x.m.After:F2} d, {x.m.Bent.Tightest:F2}>{x.m.Relaxed.Tightest:F2} r, {x.m.Bent.Slope:F2}>{x.m.Relaxed.Slope:F2}, {x.m.Bent.Crossings}/{x.m.Bent.Writhe}>{x.m.Relaxed.Crossings}/{x.m.Relaxed.Writhe}"))}"
            : $"SCENE knots: none ({marins.Why}, {knotsPath})");
        var clocks = new WallClockStage(vertices => new Mesh(device, physical, vertices));
        var parts = Robot.Meshes().Select(v => new Mesh(device, physical, v)).ToArray();
        var crate = new Mesh(device, physical, ArcadePlayer.CrateMesh()); // the small robot at the cabinet stands on it
        // The globes module triangles may be painted with (GlobeImages), read before any module's triangles are made.
        var globeClock = Stopwatch.StartNew();
        var globeDir = quality == Quality.Low && !args.Contains("--no-textures") ? GlobeImages.Find(Option(args, "--globes")) : null;
        GlobeImages.Pixels? globePixels = GlobeImages.Read(globeDir); // let go once uploaded: 130 MB of RGBA for the Earth
        SceneModuleGeometry.Images = globePixels.Held;
        Console.WriteLine(globePixels.Held.Count > 0
            ? $"SCENE globes: {string.Join(", ", globePixels.Held.Keys)}, {2 * globePixels.Size} x {globePixels.Size} px, from {globeDir}, in {globeClock.Elapsed.TotalMilliseconds:F0} ms"
            : $"SCENE globes: none{(quality != Quality.Low ? " (the Flat lighting)" : args.Contains("--no-textures") ? " (--no-textures)" : globeDir is null ? " (go -C tools/textures run . -globes only makes them)" : "")}");
        var moduleRoot = Option(args, "--module-dir") ?? Path.Combine(AppContext.BaseDirectory, "modules", "gaia");
        // The author's last validation, kept on this machine across rebuilds; an isolated --module-dir keeps its own.
        var validatedStore = Option(args, "--module-dir") is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", "modules", "gaia", "validated")
            : Path.Combine(moduleRoot, "validated");
        SceneModuleHost<Mesh>? gaiaModule = null;
        Dictionary<string, Mesh>? gaiaFallback = null;
        try
        {
            gaiaModule = new SceneModuleHost<Mesh>(Path.Combine(moduleRoot, "initial", "GaiaPresence.Module.dll"),
                Path.Combine(moduleRoot, "available", "GaiaPresence.Module.dll"), Words.Language,
                (visual, triangles, presentation) => new Mesh(device, physical, SceneModuleGeometry.Compile(visual, triangles, presentation)), () => device.WaitIdle(), validatedStore);
            Console.WriteLine($"MODULE gaia loaded code v{gaiaModule.Version.ToString(2)}; pid {Environment.ProcessId}; sha256 {gaiaModule.CodeHash}");
        }
        catch (Exception error)
        {
            gaiaFallback = GaiaPresence.Labels.ToDictionary(label => label, label => new Mesh(device, physical, GaiaPresence.Build(label)));
            Console.Error.WriteLine($"MODULE gaia unavailable ({error.GetType().Name}); keeping built-in offline presence, no external module version claimed");
        }
        ModulePickRect? gaiaPick = null;
        var modulePresentedVersion = gaiaModule?.Version;

        // The agents' office, only when --office-module names its image: a desk per Claude Code session, its widgets
        // built once and posed every frame. Without it nothing is allocated and the scene is as it was.
        WidgetModuleSession<Delta.SceneModules.IOfficeModule>? office = null;
        WidgetStage<Mesh>? officeStage = null;
        OfficeFeed? officeFeed = null;
        var officeSelection = new OfficeSelection();
        // A robot held with the left button turns its ring as the mouse moves sideways (OfficeState.Turns), half a turn
        // across the window's width; one let go within DragSlop pixels of where it was pressed is a click.
        var officeTurns = new Dictionary<string, double>(StringComparer.Ordinal);
        string? officeDrag = null;
        (double X, double Y) officeDragFrom = default, officeDragAt = default;
        var officeDragMoved = 0.0;
        const double DragSlop = 6, TurnPerWidth = Math.PI;
        var officeSpin = new OfficeSpin(); // and coasts on once let go
        var officeSpinAt = double.NaN;
        SN.Matrix4x4? officeView = null; // the frame's camera for clicks; none during the splash
        SN.Matrix4x4? chestView = null; // the last frame's torso transform and camera, for a click on Delta's chest screen
        var wallTestStep = 0; // --wall-test: the checks made
        var wallTestFailures = 0;
        var walkHeld = new HashSet<GlfwKey>(); // --walk-test: the keys it holds, read as the window's own
        var (walkStep, walkChecks, walkFailures) = (0, 0, 0);
        var walkFrom = SN.Vector3.Zero;
        int? officeLogged = null;
        var officeFailed = false; // set by the module's first exception, cleared when new code replaces it
        var origamiPlay = origamiFilmed ? Path.GetFullPath(Path.Combine(filmDir!, "origami-play.json")) : null;
        if (origamiPlay is not null) // --film-origami: its play document, written before the module reads it
        {
            Environment.SetEnvironmentVariable("DELTA_IX_ORIGAMI_PLAY", origamiPlay);
            OrigamiFilm.Prepare(origamiPlay);
        }

        if (officePath is not null)
        {
            try
            {
                office = new WidgetModuleSession<Delta.SceneModules.IOfficeModule>(officePath, "AgentOffice.Module", "Delta.AgentOffice.OfficeModule", "agent-office");
                officeStage = new WidgetStage<Mesh>(instance => new Mesh(device, physical, WidgetGeometry.Compile(instance)), FramesInFlight + 1);
                officeFeed = new OfficeFeed(demo);
                officeFeed.Start();
                Console.WriteLine($"WIDGETS agent-office loaded v{office.Version}; sha256 {office.CodeHash}; {(demo ? "demo sessions" : "reading the sessions")}; pid {Environment.ProcessId}");
            }
            catch (Exception error)
            {
                officeFeed?.Dispose();
                office?.Dispose();
                (office, officeStage, officeFeed) = (null, null, null);
                Console.Error.WriteLine($"WIDGETS agent-office refused ({error.GetType().Name}: {error.Message}); the scene goes on without it");
            }
        }
        Console.WriteLine($"SCENE geometry: hall {hallVertices.Length / 36} boxes, robot {parts.Sum(p => p.Count) / 36} boxes in {parts.Length} parts");

        // --shots: a frame's pixels, copied from the swapchain image into a host-visible buffer.
        var shotExtent = extent;
        var readbackSize = new ByteSize((nuint)(extent.Width * extent.Height * 4));
        var readback = default(VkBuffer);
        var readbackMemory = default(VkDeviceMemory);
        var readbackMapped = nint.Zero;
        if (readsBack)
        {
            Directory.CreateDirectory(shotsDir ?? filmDir!);
            readback = device.CreateBuffer(new VkBufferCreateInfo { Size = readbackSize, Usage = VkBufferUsageFlags.TransferDstBit, SharingMode = VkSharingMode.Exclusive }, null);
            readbackMemory = device.AllocateMemory(physical, readback, VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, null);
            device.BindBufferMemory(readback, readbackMemory, memoryOffset: ByteSize.Zero);
            readbackMapped = (nint)(byte*)device.MapMemory<byte>(new MapInfo(readbackMemory) { Region = new MapRegion { Size = readbackSize } });
        }

        // Per frame in flight: a command pool and buffer, a fence and an image-acquired semaphore.
        // Each frame's GPU time, from its first command to its last (two timestamps a slot), read when the slot comes
        // round again; with the time from reading the keys to the submit, what a frame takes when drawn alone.
        var frameStamps = device.CreateQueryPool(new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = 2 * FramesInFlight }, null);
        var stamped = new bool[FramesInFlight];
        var cpuOf = new double[FramesInFlight];
        var tick = properties.Limits.TimestampPeriod;
        var pools = new VkCommandPool[FramesInFlight];
        var commands = new CommandBuffer[FramesInFlight];
        var fences = new VkFence[FramesInFlight];
        var acquired = new VkSemaphore[FramesInFlight];
        for (var i = 0; i < FramesInFlight; i++)
        {
            pools[i] = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
            commands[i] = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = pools[i], Level = VkCommandBufferLevel.Primary });
            fences[i] = device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null);
            acquired[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }

        // The splash: the logo at half the window's width, its notice beneath, both at one texel a pixel.
        var splashVertex = Shader(device, SplashVertexShader, ShaderKind.VertexShader, "splash.vert");
        var splashFragment = Shader(device, SplashFragmentShader, ShaderKind.FragmentShader, "splash.frag");
        var sampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Nearest,
            MinFilter = VkFilter.Nearest,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1f,
        }, null);
        var setLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
        {
            Bindings = [new VkDescriptorSetLayoutBinding { DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit }],
        }, null);
        var descriptorPool = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
        {
            MaxSets = 25, // Google's attribution (GoogleStreets), the path-traced picture, the RTX card, the logo, the notice, the shelf's card, the caption, the phone icon, its pairing card, the ring's card and its dot, the language and microphone buttons, the guest badge, the Inviter button and its card, the microphone's meter, the version and its release notes, the cinematic's title and its hint, the wheel, the keys' card, the thinking brain and the arcade's way out
            PoolSizes = [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 25 }], // one image each
        }, null);
        var splashRange = new VkPushConstantRange { StageFlags = PushStages, Offset = ByteSize.Zero, Size = ByteSize.Of<SplashPush>() };
        var splashLayout = device.CreatePipelineLayout([setLayout], [splashRange], null);
        var splashPipeline = Pipeline(device, splashLayout, renderPass, samples, splashVertex, splashFragment, overlay: true);

        var logoPixels = Png.Load(logoPath, out var logoSourceWidth, out var logoSourceHeight);
        var logoWidth = extent.Width / 2;
        var logoHeight = (int)Math.Round(logoWidth * (double)logoSourceHeight / logoSourceWidth);
        logoPixels = Splash.Shrink(logoPixels, logoSourceWidth, logoSourceHeight, logoWidth, logoHeight);
        var noticePixels = Splash.Text(Splash.Notice, Math.Max(14, extent.Height / 40), out var noticeWidth, out var noticeHeight);
        var upload = commands[0];
        upload.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
        var materialClock = Stopwatch.StartNew();
        var materials = new MaterialTextures(device, physical, upload, materialDir);
        var globes = new GlobeImages(device, physical, upload, globePixels!);
        globePixels = null;
        var pictures = new GalleryPictures(device, physical, upload, picturePixels!);
        // Google's 3D tiles' photographs (GoogleStreets): one white layer until they are fetched, then their own array.
        var aerialImage = new MaterialTextures.ArrayImage(device, physical, upload, VkFormat.R8G8B8A8SRGB, 1, [[255, 255, 255, 255]], normal: false);
        // The demos' videos played in their globes as the eye comes near (GalleryVideo, --videos DIR, --no-videos).
        var videos = picturesDir is null || args.Contains("--no-videos") ? null
            : new GalleryVideo(device, physical, pictures, picturePixels!, GalleryVideo.Dir(Option(args, "--videos")), FramesInFlight);
        picturePixels = null;
        // A globe's halves meet at the edges: clamped there, they never blend with the far side of their own half.
        var globeSampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Linear,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            AnisotropyEnable = anisotropic,
            MaxAnisotropy = anisotropic ? 16f : 1f,
            MaxLod = 16f,
        }, null);
        // DLSS Super Resolution's mode (--dlss-sr-mode, quality by default), and the textures sampled as at the window's
        // size, as DLSS's guide asks: a mip bias of log2(drawn / window), -0.58 in quality.
        var srMode = (uint)Math.Max(Array.IndexOf(Streamline.Modes, Option(args, "--dlss-sr-mode") ?? "quality"), 1);
        var lodBias = superResolution && Streamline.SuperResolutionSize(srMode, 3840, 2160) is { } srSize ? MathF.Log2(srSize.Width / 3840f) : 0f;
        var materialSampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Linear,
            AddressModeU = VkSamplerAddressMode.Repeat,
            AddressModeV = VkSamplerAddressMode.Repeat,
            AddressModeW = VkSamplerAddressMode.Repeat,
            MipLodBias = lodBias,
            AnisotropyEnable = anisotropic,
            MaxAnisotropy = anisotropic ? 16f : 1f,
            MaxLod = 16f,
        }, null);
        Console.WriteLine(materials.Photographed
            ? $"SCENE materials: {MaterialTextures.Names.Length} photographed, {materials.Size} px, {(anisotropic ? "16x anisotropic" : "no anisotropy")}, from {materialDir}, in {materialClock.Elapsed.TotalMilliseconds:F0} ms"
            : $"SCENE materials: flat colours{(quality != Quality.Low ? " (the Flat lighting)" : args.Contains("--no-textures") ? " (--no-textures)" : materialDir is null ? " (no textures; go -C tools/textures run . fetches them)" : "")}");
        Texture[] splashTextures = noticePixels is null
            ? [new Texture(device, physical, upload, logoPixels, logoWidth, logoHeight)]
            : [new Texture(device, physical, upload, logoPixels, logoWidth, logoHeight), new Texture(device, physical, upload, noticePixels, noticeWidth, noticeHeight)];
        var arcadeGpu = arcade is null ? null : new ScreenGpuRenderer(device, physical, upload, renderPass, samples, sampler, setLayout, FramesInFlight, arcade.Frame);
        if (arcade is not null) Console.WriteLine($"ARCADE loaded {arcade.Id} v{arcade.Version}; sha256 {arcade.CodeHash}; canvas 160x96 RGBA8; GPU images {arcadeGpu!.ImageCount}; pid {Environment.ProcessId}");
        // The arcade cabinet and the web game on its screen (ArcadeCabinet); --arcade-url plays another page's #cv canvas.
        var cabinet = new ArcadeStage(vertices => new Mesh(device, physical, vertices),
            (pixels, width, height, revision, rect, scanlines) => new ScreenGpuRenderer(device, physical, upload, renderPass, samples, sampler, setLayout, FramesInFlight, pixels, width, height, revision, rect, 0f, scanlines),
            Option(args, "--arcade-url"));
        // What the wall screen shows (WallSource): Parsec through the cabinet, or Pong's frames, or a session's card, the
        // last two in surfaces of their own at the wall's frame, Pong letterboxed to its 5:3.
        var wall = new WallSource();
        var (wallW, wallH) = (WallSource.Width / 2f, WallSource.Height / 2f);
        var sessionWall = new ScreenGpuRenderer(device, physical, upload, renderPass, samples, sampler, setLayout, FramesInFlight,
            WallCard.Render([], ""), WallCard.Width, WallCard.Height, 1, new(-wallW, wallH, wallW, -wallH), 0f, 0f);
        // The lectern's open book (LearnLectern): a course or a Streeling department read in the hall, its pages a picture
        // laid out by the reader's mod, reloaded when a build replaces modules\reader\LearnReader.Module.dll.
        var reader = new LearnLectern(new LearnHost(HallGallery.Browse), Option(args, "--reader-module") ?? Path.Combine(AppContext.BaseDirectory, "modules", "reader", "LearnReader.Module.dll"));
        var firstPage = reader.Picture;
        var readerBook = new ScreenGpuRenderer(device, physical, upload, renderPass, samples, sampler, setLayout, FramesInFlight,
            firstPage.Rgba, Delta.SceneModules.ReaderPicture.Width, Delta.SceneModules.ReaderPicture.Height, firstPage.Revision, HallGallery.BookRect, 0f, 0f);
        // --read course:N or department:N opens that book on the lectern from the start, for captures (with --view 9).
        if (Option(args, "--read") is { } toRead)
        {
            ReadOnLectern(toRead);
        }
        var pongWall = arcade is { Frame: { Stride: var stride, Width: var pw, Height: var ph } pong } && stride == pw * 4
            ? new ScreenGpuRenderer(device, physical, upload, renderPass, samples, sampler, setLayout, FramesInFlight, pong.Rgba.ToArray(), pw, ph,
                pong.Revision, new(-wallW, wallW * ph / pw, wallW, -wallW * ph / pw), 0f, 0.2f)
            : null;
        var wallLines = new List<string>();
        var wallCardRevision = 1UL;
        var wallCardAt = double.NegativeInfinity;
        var wallReadAt = double.NegativeInfinity;
        var wallPixels = WallCard.Render([], "");
        WallSource.Transcript? wallTranscript = null;
        var wallReading = 0; // a transcript read in flight
        upload.End();
        var uploadHandle = upload.Handle;
        queue.Submit([new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &uploadHandle }], default);
        device.WaitIdle();
        materials.FreeStaging();
        globes.FreeStaging();
        pictures.FreeStaging();
        aerialImage.FreeStaging();
        // Path tracing (F9, --path-tracing), with the ray scene: the hall drawn by following light through it.
        if (rays is not null && rtPipeline)
        {
            rays.ReadBy |= VkPipelineStageFlags.RayTracingShaderBitKHR;
        }

        var tracer = rays is not null
            ? new PathTracer(device, physical, FramesInFlight, rays, materials.Albedo, materials.Normal, materials.Arm, materialSampler, globes.View, globeSampler, descriptorPool, setLayout,
                rtPipeline, reorder, properties.Limits.TimestampPeriod)
            : null;
        Console.WriteLine(tracer is not null ? $"SCENE path tracing: offered, by {tracer.How}, {PathTracer.Bounces} bounces, kept {PathTracer.MaxHistory} frames; F9 or --path-tracing" : "SCENE path tracing: none (no ray queries)");
        if (tracer is not null && rayReconstruction)
        {
            // --dlss: the path tracer through Ray Reconstruction, in --dlss-mode's mode (performance by default).
            var mode = Array.IndexOf(Streamline.Modes, Option(args, "--dlss-mode") ?? "performance");
            tracer.UpscaleMode = (uint)Math.Max(mode, 1);
            tracer.JitterSign = args.Contains("--dlss-jitter-flip") ? -1f : 1f;
            Console.WriteLine($"SCENE path tracing through DLSS Ray Reconstruction, {Streamline.Modes[tracer.UpscaleMode]}");
        }

        // DLSS Super Resolution on the raster pass (the window drawn without path tracing), in --dlss-sr-mode's mode
        // (quality by default); F7 turns it off and on.
        var upscaler = superResolution ? new RasterUpscaler(device, physical, surfaceFormat.Format, depthFormat, dependency, setLayout) : null;
        var upscaling = upscaler is not null;
        // The gallery's water globes and paperweights (GlassWork): bent through the hall drawn so far.
        var glass = new GlassWork(device, physical, renderPass, samples, surfaceFormat.Format, setLayout);
        var glassWay = (GlassWork.Way)(-1);
        if (upscaler is not null)
        {
            upscaler.Mode = srMode;
            Console.WriteLine($"SCENE raster pass through DLSS Super Resolution, {Streamline.Modes[upscaler.Mode]}, textures' mip bias {lodBias:F2}; F7 or --no-dlss-sr");
        }
        var maps = stackalloc VkDescriptorImageInfo[6];
        foreach (var frameSet in frameSets)
        {
            maps[0] = new VkDescriptorImageInfo { Sampler = materialSampler, ImageView = materials.Albedo, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            maps[1] = new VkDescriptorImageInfo { Sampler = materialSampler, ImageView = materials.Normal, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            maps[2] = new VkDescriptorImageInfo { Sampler = materialSampler, ImageView = materials.Arm, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            maps[3] = new VkDescriptorImageInfo { Sampler = globeSampler, ImageView = globes.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            maps[4] = new VkDescriptorImageInfo { Sampler = globeSampler, ImageView = pictures.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            maps[5] = new VkDescriptorImageInfo { Sampler = globeSampler, ImageView = aerialImage.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            device.UpdateDescriptorSets(
            [
                new VkWriteDescriptorSet { DstSet = frameSet, DstBinding = 7, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[5] },
                new VkWriteDescriptorSet { DstSet = frameSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[0] },
                new VkWriteDescriptorSet { DstSet = frameSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[1] },
                new VkWriteDescriptorSet { DstSet = frameSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[2] },
                new VkWriteDescriptorSet { DstSet = frameSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[3] },
                new VkWriteDescriptorSet { DstSet = frameSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &maps[4] },
            ], []);
        }
        var splashSets = new VkDescriptorSet[splashTextures.Length];
        for (var i = 0; i < splashTextures.Length; i++)
        {
            splashTextures[i].FreeStaging();
            splashSets[i] = device.AllocateDescriptorSet(descriptorPool, setLayout);
            var imageInfo = new VkDescriptorImageInfo { Sampler = sampler, ImageView = splashTextures[i].View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = splashSets[i], DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &imageInfo }], []);
        }

        // Each rectangle in whole pixels, so that a texel lands on a pixel; the notice 36 px under the logo.
        var logoTop = ((extent.Height - logoHeight) / 2) - 24;
        SN.Vector4 Rect(int left, int top, int width, int height) => new(
            (2f * left / extent.Width) - 1f, (2f * top / extent.Height) - 1f, (2f * (left + width) / extent.Width) - 1f, (2f * (top + height) / extent.Height) - 1f);
        SN.Vector4[] splashRects =
        [
            Rect((extent.Width - logoWidth) / 2, logoTop, logoWidth, logoHeight),
            Rect((extent.Width - noticeWidth) / 2, logoTop + logoHeight + 36, noticeWidth, noticeHeight),
        ];
        SN.Vector3[] splashTints = [SN.Vector3.One, new(0.62f)];
        Console.WriteLine($"SCENE splash: BRUTAL logo {logoWidth}x{logoHeight} px from {logoSourceWidth}x{logoSourceHeight}, notice {(noticePixels is null ? "on the console only" : $"{noticeWidth}x{noticeHeight} px")}");

        var placement = new Placement();
        link.Window = placement.Ask;
        var tray = OperatingSystem.IsWindows() ? new Tray() : null;
        if (tray is not null)
        {
            tray.Clicked += () => placement.Ask("show");
        }

        var stale = false; // the swapchain no longer matches the window
        var gap = new FrameGap(); // what fills the time between two frames, for a slow frame's line
        window.OnFramebufferSize += (_, _) =>
        {
            stale = true;
            gap.Saw(clock.Elapsed.TotalSeconds, $"framebuffer {window.FramebufferSize.X}x{window.FramebufferSize.Y}");
        };
        window.OnIconify += (_, _) => gap.Saw(clock.Elapsed.TotalSeconds, window.IsIconified ? "minimised" : "restored");
        window.OnMaximize += (_, _) => gap.Saw(clock.Elapsed.TotalSeconds, "maximised or restored");
        window.OnPosition += (_, _) => gap.Saw(clock.Elapsed.TotalSeconds, "moved");
        window.OnRefresh += _ => gap.Saw(clock.Elapsed.TotalSeconds, "asked to redraw");

        // The shelf (Shelf.cs): M opens it (Tab, which did too, is the camera's wheel); the arrows, Enter, Backspace and the mouse move through it; Esc
        // closes it, never the window. Its card is drawn again when what it shows changes.
        var shelf = new Shelf();
        var cardSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        var uploadPool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = family }, null);
        var uploader = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = uploadPool, Level = VkCommandBufferLevel.Primary });
        Texture? cardTexture = null;
        Panel.Card? card = null;
        var cardVersion = -1;
        var cardHeight = 0;
        int CardMargin() => Math.Max(12, extent.Height / 48);

        // The caption under the robot (Panel.Caption) for each sentence Delta heard, drawn again for each and for a new size.
        var captionSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? captionTexture = null;
        var aerialCardSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? aerialCardTexture = null;
        Panel.Card? aerialCard = null;
        var aerialCardSize = (0, 0);
        Panel.Card? caption = null;
        var captionId = 0;
        var captionSize = (0, 0);

        // The phone icon in the top right corner (Panel.Phone), lit while a pairing code lives, and the card a click on it
        // or P opens and closes (Panel.Pairing). The code is a credential while it lives, so it shows only when asked, as
        // the window may be shared.
        var phoneSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? phoneTexture = null;
        Panel.Card? phone = null;
        var phoneDrawn = (Live: false, Connected: false, Height: 0);
        var pairingSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? pairingTexture = null;
        Panel.Card? pairingCard = null;
        var pairingDrawn = (Id: -1, Left: -1, Width: 0, Height: 0, Language: "");
        var pairingShown = false;
        var pairingOpened = 0.0; // when the card last opened: it closes by itself a minute later, since a device that
                                 // only watches the window, such as a tablet, cannot close it
        void ShowPairing(bool show)
        {
            pairingShown = show;
            pairingOpened = clock.Elapsed.TotalSeconds;
            if (show && !demo && link.Read(clock.Elapsed.TotalSeconds).Pairing is null)
            {
                link.Tell("""{"pair":true}"""); // no code alive: the brain asks the phone gateway for one, which comes as a pairing line
            }
        }
        (int X, int Y) PhoneAt() => (extent.Width - Panel.PhoneSize(extent.Height) - CardMargin(), CardMargin());
        // The language button left of the phone icon (Panel.LanguageButton): a click, or L, picks the next setting, which
        // the brain applies and echoes back; with no brain, the window takes it at once.
        var languageSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? languageTexture = null;
        Panel.Card? languageButton = null;
        var languageDrawn = (Setting: "", Height: 0);
        (int X, int Y) LanguageAt() => (PhoneAt().X - (languageButton?.Width ?? 0) - Math.Max(6, extent.Height / 96), CardMargin());
        // The microphone button left of the language button (Panel.MicButton): a click turns the computer's microphone off
        // or on again, as the brain echoes back.
        var micSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? micTexture = null;
        Panel.Card? micButton = null;
        var micDrawn = (Off: false, Height: 0);
        (int X, int Y) MicAt() => (LanguageAt().X - Panel.PhoneSize(extent.Height) - Math.Max(6, extent.Height / 96), CardMargin());
        // The brain's version in the bottom right corner (Panel.VersionLabel).
        var versionSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? versionTexture = null;
        Panel.Card? versionLabel = null;
        var versionDrawn = (Version: "", Height: 0, Language: "");
        (int X, int Y) VersionAt() => versionLabel is null ? (0, 0) : (extent.Width - versionLabel.Width - CardMargin(), extent.Height - versionLabel.Height - CardMargin());

        // The microphone's meter along the button's bottom edge: a green bar as long as the voice is loud, from -60 dB to
        // -10 dB, drawn from a white square (Panel.Blank).
        var meterSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? meterTexture = null;
        void ToggleMic(bool turnOn)
        {
            if (!link.Tell($$"""{"mic":{{(turnOn ? "true" : "false")}}}"""))
            {
                link.Apply(new Link.Change(null, [], "", null, null, Mic: turnOn), clock.Elapsed.TotalSeconds);
            }
        }

        // The guest badge at the top centre while guests watch from afar (Panel.GuestBadge).
        var guestSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? guestTexture = null;
        Panel.Card? guestBadge = null;
        var guestDrawn = (Watching: 0, Hear: false, Height: 0, Language: "");
        (int X, int Y) GuestAt() => guestBadge is null ? (0, 0) : ((extent.Width - guestBadge.Width) / 2, CardMargin());

        // The Inviter button left of the microphone button (Panel.InviteButton): a click asks the brain for an invitation,
        // whose card (Panel.Invitation) opens when it comes. Guests watch this window, so the card closes as soon as one
        // does, and by itself a minute later, as the pairing card.
        var inviteSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? inviteTexture = null;
        Panel.Card? inviteButton = null;
        var inviteDrawn = (Height: 0, Language: "");
        (int X, int Y) InviteAt() => (MicAt().X - (inviteButton?.Width ?? 0) - Math.Max(6, extent.Height / 96), CardMargin());
        var invitationSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? invitationTexture = null;
        Panel.Card? invitationCard = null;
        var invitationDrawn = (Id: -1, Width: 0, Height: 0, Language: "");
        var invitationShown = false;
        var invitationSeen = 0; // the last invitation that opened the card
        var invitationOpened = 0.0;
        (int X, int Y) InvitationAt() => invitationCard is null ? (0, 0) : ((extent.Width - invitationCard.Width) / 2, (extent.Height - invitationCard.Height) / 2);

        // The release notes a click on the version opens, or N (Panel.ReleaseNotes); a click on them or Esc closes them.
        var notesSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? notesTexture = null;
        Panel.Card? notesCard = null;
        var notesDrawn = (Version: "", Width: 0, Height: 0, Language: "");
        var notesShown = false;
        (int X, int Y) NotesAt() => notesCard is null ? (0, 0) : ((extent.Width - notesCard.Width) / 2, (extent.Height - notesCard.Height) / 2);
        void ShowNotes(bool show)
        {
            notesShown = show;
            pairingShown &= !show;
            invitationShown &= !show;
        }


        void NextLanguage()
        {
            var next = Words.Next(Words.Setting);
            if (!link.Tell($$"""{"language":"{{next}}"}"""))
            {
                link.Apply(new Link.Change(null, [], "", null, null, Language: next), clock.Elapsed.TotalSeconds);
            }
        }
        // The ring in the bottom left corner while Delta listens without its name: its card (Panel.Lease), drawn once for
        // the window's size, and one dot (Panel.Dot) drawn LeaseDots times over it, tinted lit or dim as it empties.
        var leaseSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? leaseTexture = null;
        Panel.Card? leaseCard = null;
        var leaseDotSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? leaseDotTexture = null;
        Panel.Card? leaseDot = null;
        var leaseDrawn = (Height: 0, Language: "");
        // While Claude thinks, a small brain beside the ring (Panel.Brain), beating from when the thinking began.
        var brainSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? brainTexture = null;
        Panel.Card? brainCard = null;
        var brainDrawn = 0; // the window's height it was drawn for
        var thinkingSince = double.NegativeInfinity;
        var thinkingWas = false;
        (int X, int Y) PairingAt() => pairingCard is null ? (0, 0) : ((extent.Width - pairingCard.Width) / 2, (extent.Height - pairingCard.Height) / 2);

        // Uploads a drawn card into a new texture that set samples, once no frame in flight reads the old one.
        Texture Upload(Panel.Card drawn, VkDescriptorSet set, Texture? old)
        {
            device.WaitIdle();
            device.ResetCommandPool(uploadPool, VkCommandPoolResetFlags.None);
            uploader.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
            var fresh = new Texture(device, physical, uploader, drawn.Rgba, drawn.Width, drawn.Height);
            uploader.End();
            var uploaderHandle = uploader.Handle;
            queue.Submit([new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &uploaderHandle }], default);
            device.WaitIdle();
            fresh.FreeStaging();
            old?.Dispose();
            var image = new VkDescriptorImageInfo { Sampler = sampler, ImageView = fresh.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = set, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image }], []);
            return fresh;
        }

        // Google's tiles' photographs, once fetched: their array in place of the white layer, in every frame's set.
        void UploadAerial(byte[][] layers)
        {
            device.WaitIdle();
            device.ResetCommandPool(uploadPool, VkCommandPoolResetFlags.None);
            uploader.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
            var fresh = new MaterialTextures.ArrayImage(device, physical, uploader, VkFormat.R8G8B8A8SRGB, GoogleStreets.Side, layers, normal: false);
            uploader.End();
            var uploaderHandle = uploader.Handle;
            queue.Submit([new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &uploaderHandle }], default);
            device.WaitIdle();
            fresh.FreeStaging();
            aerialImage.Dispose();
            aerialImage = fresh;
            var image = new VkDescriptorImageInfo { Sampler = globeSampler, ImageView = fresh.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            foreach (var frameSet in frameSets)
            {
                device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = frameSet, DstBinding = 7, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image }], []);
            }
        }
        link.Shelf = command =>
        {
            var at = clock.Elapsed.TotalSeconds;
            switch (command)
            {
                case "open":
                    shelf.Show(at);
                    break;
                case "sessions" or "conversations" or "plugins" or "marketplaces" or "courses":
                    shelf.Show(at, command);
                    break;
                case "close":
                    shelf.Hide();
                    break;
                case "up":
                    shelf.Move(at, -1);
                    break;
                case "down":
                    shelf.Move(at, 1);
                    break;
                case "enter":
                    shelf.Enter(at);
                    break;
                case "back":
                    shelf.Back(at);
                    break;
            }
        };

        if (shelfAtStart is not null)
        {
            shelf.Show(0, shelfAtStart);
        }

        var reason = "window closed";
        // Runs what a key, a click or a scroll asks, or draws a card; an exception there skips it, in the log, rather than
        // closing the window.
        void Guard(string what, Action act)
        {
            try
            {
                act();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"SCENE {what} failed and was skipped: {e}");
            }
        }

        var reducedMotion = args.Contains("--reduced-motion");
        var camera = new FreeCamera { ReducedMotion = reducedMotion };
        if (int.TryParse(Option(args, "--view"), out var startView) && !camera.GoTo(startView))
        {
            Console.Error.WriteLine($"SCENE no view {startView}; the camera drifts");
        }

        // --door inside|street|back: the roll-up door open from the start, the streets loading, and the camera looking
        // out through it from the hall, up the street from outside, or back at the hall; for hidden checks.
        if (Option(args, "--door") is { } doorShot)
        {
            streetDoor.OpenNow();
            streetsLoading = OttawaStreets.Load();
            camera.DoorOpen = true;
            var (doorEye, doorTarget) = doorShot switch
            {
                "street" => (new SN.Vector3(HallShell.DoorX, FreeCamera.WalkHeight, HallShell.Front + 6f), new SN.Vector3(HallShell.DoorX, 6f, HallShell.Front + 90f)),
                "back" => (new SN.Vector3(HallShell.DoorX + 4f, FreeCamera.WalkHeight + 1f, HallShell.Front + 14f), new SN.Vector3(HallShell.DoorX, 2f, HallShell.Front)),
                "air" => (new SN.Vector3(HallShell.DoorX + 30f, 50f, HallShell.Front + 2f), new SN.Vector3(HallShell.DoorX, 8f, HallShell.Front + 120f)),
                "lawn" => (new SN.Vector3(HallShell.DoorX + 6f, FreeCamera.WalkHeight, HallShell.Front + 80f), new SN.Vector3(HallShell.DoorX - 4f, 30f, HallShell.Front + 140f)),
                _ => (new SN.Vector3(HallShell.DoorX, FreeCamera.WalkHeight, 1.5f), new SN.Vector3(HallShell.DoorX, 1.8f, 20f)),
            };
            camera.GoTo(doorEye, doorTarget, cut: true);
        }

        var film = filmDir is null || origamiFilmed ? new Cinematic { ReducedMotion = reducedMotion } : new Cinematic(ArcadeFilm.Shots, ArcadeFilm.Heading); // 0, or --cinematic once the splash is over
        var arcadeFilm = filmDir is null || origamiFilmed ? null : new ArcadeFilm(camera, film);
        var origamiFilm = origamiPlay is null ? null : new OrigamiFilm(camera, origamiPlay, origamiSeconds);
        var filmAsked = false; // the film's game asked of the cabinet
        var hud = filmDir is null; // the window's buttons and cards; a film shows none
        // While nobody moves the camera it tours the hall (IdleTour); --no-tour leaves it out, as films and shots do.
        // --tour-after S starts the tour after S seconds instead of 25, for captures. PARSEC is a third party's page, fetched
        // from its site and run in the scene, so nothing fetches it until asked: a click on the cabinet, or --arcade-robot,
        // with which the small robot plays it at the cabinet while nobody plays, silent, the wall showing its game.
        var tour = new IdleTour(camera, () => cabinet.Broadcasting)
        {
            After = double.TryParse(Option(args, "--tour-after"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tourAfter) ? tourAfter : IdleTour.IdleSeconds,
        };
        var touring = filmDir is null && !demo && !args.Contains("--no-tour");
        var robotPlays = filmDir is null && !demo && args.Contains("--arcade-robot");
        var robotAt = 0.0; // when the robot may next take the cabinet
        var arcadePlayer = new ArcadePlayer();
        var filmAtStart = args.Contains("--cinematic") || Option(args, "--cinematic-from") is not null;
        var filmFrom = int.TryParse(Option(args, "--cinematic-from"), out var filmFromValue) ? filmFromValue : 0; // a later shot first, for captures
        var filmView = int.TryParse(Option(args, "--film-view"), out var filmViewValue) ? filmViewValue : 0; // a view's shot alone, as Shift and its key, for captures
        var landOn = 0; // the view the player's camera cuts to once a view's shot is in black
        var wheel = new ToolWheel(); // Tab; --wheel opens it once the splash is over, for captures
        var wheelAtStart = args.Contains("--wheel");
        var arcadeAtStart = args.Contains("--arcade-play"); // the cabinet's game once the splash is over, for captures
        var flyTo = int.TryParse(Option(args, "--fly-to"), out var flyToValue) ? flyToValue : 0; // a view's flight half a second after the splash, for captures
        // --resize-to WxH resizes the window a second after the splash, to measure the rebuild a live window's maximising makes
        var resizeTo = Option(args, "--resize-to")?.Split('x') is [var rw, var rh] && int.TryParse(rw, out var resizeW) && int.TryParse(rh, out var resizeH) && resizeW > 0 && resizeH > 0
            ? new int2(resizeW, resizeH)
            : default(int2?);
        var modalDrill = args.Contains("--modal-drill"); // the window's measuring run of a held title bar and a dragged border (ModalDrill.cs)
        var keysShown = args.Contains("--keys"); // F1: the camera's keys
        var hazy = !args.Contains("--no-fog"); // B: the Low lighting's haze
        var raysOn = !args.Contains("--rays-off"); // R: traced shadows and occlusion, or painted (with rays offered)
        var pathTracing = tracer is not null && args.Contains("--path-tracing"); // F9: the hall path traced (PathTracer)

        // F6 or --max-out, as the Observatory's Max out: every effect at its best, the path tracer with more bounces and
        // paths, the haze and the traced rays on; again, back to what it found. Without DLSS or frame generation, which
        // the scene has not, the frame rate is what the GPU draws.
        var maxOut = false;
        (bool PathTracing, bool Hazy, bool RaysOn) beforeMax = default;
        void MaxOut(bool on)
        {
            if (tracer is null || on == maxOut)
            {
                return;
            }

            if (on)
            {
                beforeMax = (pathTracing, hazy, raysOn);
                (pathTracing, hazy, raysOn) = (true, true, true);
            }
            else
            {
                (pathTracing, hazy, raysOn) = beforeMax;
            }

            maxOut = on;
            tracer.MaxOut = on;
            tracer.Forget();
            Console.WriteLine(on
                ? $"SCENE max out: path traced, {PathTracer.MaxOutBounces} bounces, {PathTracer.MaxOutSamples} paths a pixel, haze and rays on"
                : "SCENE max out off: back to the settings before");
        }

        MaxOut(args.Contains("--max-out"));
        var looking = false; // the right button is held
        var locked = false; // Ctrl and a click lock the view to the mouse, the Observatory's click; again, or the right button, frees it
        var ctrlAlone = false; // Ctrl down with nothing else since: let go, it crouches
        var sceneClock = clock.Elapsed.TotalSeconds; // the scene's time, which the open wheel slows to a quarter
        (double X, double Y) lookFrom = default;
        const int CursorNormal = 0x00034001, CursorDisabled = 0x00034003; // GLFW_CURSOR_NORMAL, GLFW_CURSOR_DISABLED
        const int ModShift = 0x0001, ModControl = 0x0002; // GLFW_MOD_SHIFT, GLFW_MOD_CONTROL
        void Grab(GlfwWindow w)
        {
            w.SetInputMode(GlfwInputMode.Cursor, looking || locked ? CursorDisabled : CursorNormal);
            lookFrom = (w.CursorX, w.CursorY);
        }

        // The cinematic's title and hint, the wheel and the keys' card, drawn as the shelf's card is (Panel).
        var filmTitleSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? filmTitleTexture = null;
        Panel.Card? filmTitle = null;
        var filmHintSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? filmHintTexture = null;
        Panel.Card? filmHint = null;
        var arcadeHintSet = device.AllocateDescriptorSet(descriptorPool, setLayout); // the way out of the cabinet's game
        Texture? arcadeHintTexture = null;
        Panel.Card? arcadeHint = null;
        var arcadeHintDrawn = (Height: 0, Language: "");
        var filmDrawn = (Height: 0, Language: "");
        var wheelSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? wheelTexture = null;
        Panel.Card? wheelCard = null;
        var wheelDrawn = (Pick: -2, Width: 0, Height: 0, Language: "");
        var keysSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? keysTexture = null;
        Panel.Card? keysCard = null;
        var keysDrawn = (Flying: false, Width: 0, Height: 0, Language: "");
        var rtxShown = args.Contains("--rtx"); // F10: what the GPU offers of RTX and what the scene uses
        var rtxSet = device.AllocateDescriptorSet(descriptorPool, setLayout);
        Texture? rtxTexture = null;
        Panel.Card? rtxCard = null;
        var rtxDrawn = (Rays: false, Path: false, Max: false, Fps: -1, Height: 0, Language: "");
        var rtxFps = 0.0; // frames a second, averaged over about a second
        List<Panel.RtxRow> RtxRows() =>
        [
            new("Max out",
                maxOut ? Panel.Rtx.Use : tracer is not null ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                Words.Say($"every effect at its best: path traced, {PathTracer.MaxOutBounces} bounces, {PathTracer.MaxOutSamples} paths a pixel · F6",
                    $"tous les effets au maximum : path tracing, {PathTracer.MaxOutBounces} rebonds, {PathTracer.MaxOutSamples} chemins par pixel · F6")),
            new(Words.Say("Ray-traced shadows and occlusion", "Ombres et occlusion tracées"),
                rays is not null && raysOn && !pathTracing ? Panel.Rtx.Use : RayScene.Offered(physical, rtxOffered) ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                rays is not null
                    ? Words.Say("VK_KHR_ray_query, 3 shadow and 5 occlusion rays a pixel · R: on or off", "VK_KHR_ray_query, 3 rayons d'ombre et 5 d'occlusion par pixel · R : activer ou couper")
                    : Words.Say("VK_KHR_ray_query · Low lighting without --no-rays", "VK_KHR_ray_query · éclairage Low sans --no-rays")),
            new("Path tracing",
                tracer is not null && pathTracing ? Panel.Rtx.Use : tracer is not null || RayScene.Offered(physical, rtxOffered) ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                Words.Say($"{(maxOut ? PathTracer.MaxOutBounces : PathTracer.Bounces)} bounces, kept {PathTracer.MaxHistory} frames, filtered · F9: on or off",
                    $"{(maxOut ? PathTracer.MaxOutBounces : PathTracer.Bounces)} rebonds, gardé {PathTracer.MaxHistory} images, filtré · F9 : activer ou couper")),
            new(Words.Say("Ray tracing pipeline", "Pipeline de lancer de rayons"),
                tracer is { Pipeline: true } && pathTracing ? Panel.Rtx.Use : Has("VK_KHR_ray_tracing_pipeline") ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                tracer is { Pipeline: true }
                    ? Words.Say("VK_KHR_ray_tracing_pipeline · the path tracer's ray generation shader", "VK_KHR_ray_tracing_pipeline · le shader de génération du path tracer")
                    : "VK_KHR_ray_tracing_pipeline"),
            new(Words.Say("Shader execution reordering", "Réordonnancement des shaders (SER)"),
                tracer is { Reordering: true } && pathTracing ? Panel.Rtx.Use : Has("VK_NV_ray_tracing_invocation_reorder") ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                tracer is { Reordering: true }
                    ? Words.Say("VK_NV_ray_tracing_invocation_reorder · each bounce's rays sorted by the object they hit (--ser)", "VK_NV_ray_tracing_invocation_reorder · les rayons de chaque rebond triés par l'objet touché (--ser)")
                    : "VK_NV_ray_tracing_invocation_reorder"),
            new(Words.Say("Opacity micromaps", "Micro-cartes d'opacité"),
                Has("VK_EXT_opacity_micromap") ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                Words.Say("VK_EXT_opacity_micromap · for alpha-tested leaves and fences; the hall has none", "VK_EXT_opacity_micromap · pour le feuillage et les grillages découpés ; la salle n'en a pas")),
            new("DLSS (Super Resolution, Ray Reconstruction)",
                (tracer is { Upscaling: true } && pathTracing) || (upscaler is { Ready: true } && upscaling && !pathTracing) ? Panel.Rtx.Use
                    : tracer is { UpscaleMode: > 0 } || upscaler is not null || (nvidia && Has("VK_NVX_binary_import") && Has("VK_NVX_image_view_handle")) ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                upscaler is { Ready: true } u && upscaling && !pathTracing
                    ? Words.Say($"Super Resolution ({Streamline.Modes[u.Mode]}): the hall drawn at {u.Width}x{u.Height}, upscaled to {u.OutputWidth}x{u.OutputHeight}, the cards at full size · F7: on or off",
                        $"Super Resolution ({Streamline.Modes[u.Mode]}) : la salle dessinée à {u.Width}x{u.Height}, agrandie à {u.OutputWidth}x{u.OutputHeight}, les cartes en pleine taille · F7 : activer ou couper")
                    : tracer is { UpscaleMode: > 0 } t
                    ? Words.Say($"Ray Reconstruction ({Streamline.Modes[t.UpscaleMode]}): the path tracer at {t.Width}x{t.Height}, denoised and upscaled to {t.OutputWidth}x{t.OutputHeight} (--dlss)",
                        $"Ray Reconstruction ({Streamline.Modes[t.UpscaleMode]}) : le path tracer à {t.Width}x{t.Height}, débruité et agrandi à {t.OutputWidth}x{t.OutputHeight} (--dlss)")
                    : Words.Say("NGX's extensions, VK_NVX_binary_import and VK_NVX_image_view_handle; --dlss with NVIDIA's Streamline", "extensions de NGX, VK_NVX_binary_import et VK_NVX_image_view_handle ; --dlss avec Streamline de NVIDIA")),
            new(Words.Say("DLSS Frame Generation", "Génération d'images DLSS"),
                Streamline.Generating ? Panel.Rtx.Use : frameGeneration || (nvidia && Has("VK_NV_optical_flow")) ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                frameGeneration
                    ? Words.Say($"{Streamline.FramesToGenerate} frame(s) generated between two path-traced, {Streamline.FramesPerDrawn} presented for each drawn (--dlss-g)",
                        $"{Streamline.FramesToGenerate} image(s) générée(s) entre deux tracées, {Streamline.FramesPerDrawn} présentée(s) pour chaque dessinée (--dlss-g)")
                    : Words.Say("VK_NV_optical_flow; --dlss-g, with the path tracer and Ray Reconstruction", "VK_NV_optical_flow ; --dlss-g, avec le path tracer et Ray Reconstruction")),
            new("Reflex",
                reflex is { LowLatency: true } || Streamline.Generating ? Panel.Rtx.Use : Has("VK_NV_low_latency2") ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                Streamline.Generating
                    ? Words.Say("through Streamline, low latency mode, with frame generation", "par Streamline, mode basse latence, avec la génération d'images")
                    : reflex is { LowLatency: true }
                    ? Words.Say("VK_NV_low_latency2 · low latency mode with boost, pacing the frames (--reflex)", "VK_NV_low_latency2 · mode basse latence avec boost, qui règle les images (--reflex)")
                    : Words.Say("VK_NV_low_latency2; --reflex uses it in place of the present wait", "VK_NV_low_latency2 ; --reflex l'utilise à la place de l'attente de présentation")),
            new(Words.Say("Present wait", "Attente de présentation"),
                paceByScreen ? Panel.Rtx.Use : canWait ? Panel.Rtx.Offered : Panel.Rtx.Absent,
                "VK_KHR_present_id, VK_KHR_present_wait"),
            new("MSAA 4x", msaa ? Panel.Rtx.Use : Panel.Rtx.Offered, Words.Say("four samples a pixel, Low lighting", "quatre échantillons par pixel, éclairage Low")),
        ];

        GlfwKeyCallback onKey = (w, key, scancode, action, mods) => Guard("a key", () =>
        {
            var at = clock.Elapsed.TotalSeconds;
            tour.Touch(at);
            if (cabinet.Playing)
            {
                // At the cabinet every key is the game's, but Esc, which leaves it for the hall's first view (the
                // player is not left facing the cabinet); the robot comes back a while after.
                if (key == GlfwKey.Escape)
                {
                    if (action == GlfwKeyAction.Press)
                    {
                        LeaveArcade(at);
                    }
                }
                else if (action != GlfwKeyAction.Repeat)
                {
                    cabinet.Key(key, action == GlfwKeyAction.Press);
                }

                ctrlAlone = false;
                return;
            }

            if (action == GlfwKeyAction.Release)
            {
                if (key == GlfwKey.Tab)
                {
                    wheel.TabUp(at);
                }
                else if (key is GlfwKey.LeftControl or GlfwKey.RightControl && ctrlAlone && !shelf.Open)
                {
                    camera.Crouch((float)sceneClock);
                }

                ctrlAlone = false;
                return;
            }

            ctrlAlone = action == GlfwKeyAction.Press && key is GlfwKey.LeftControl or GlfwKey.RightControl;
            if (film.Playing)
            {
                if (action == GlfwKeyAction.Press)
                {
                    film.Skip(); // any key ends the cinematic, Esc too, rather than the window
                }

                ctrlAlone = false;
                return;
            }

            if (shelf.Open)
            {
                switch (key)
                {
                    case GlfwKey.Escape or GlfwKey.Tab:
                        shelf.Hide();
                        break;
                    case GlfwKey.Up:
                        shelf.Move(at, -1);
                        break;
                    case GlfwKey.Down:
                        shelf.Move(at, 1);
                        break;
                    case GlfwKey.Enter or GlfwKey.Right:
                        shelf.Enter(at);
                        break;
                    case GlfwKey.Backspace or GlfwKey.Left:
                        shelf.Back(at);
                        break;
                }

                return;
            }

            if (action == GlfwKeyAction.Repeat)
            {
                return; // a key held is read each frame (the loop); its repeats toggle nothing
            }

            if (key == GlfwKey.Escape && wheel.Open)
            {
                wheel.Close(false);
            }
            else if (key == GlfwKey.Tab)
            {
                wheel.TabDown(at);
            }
            else if (key == GlfwKey.F)
            {
                camera.ToggleFlying((float)sceneClock);
            }
            else if (key == GlfwKey.Space)
            {
                camera.Jump((float)sceneClock); // flying, Space rises (the loop)
            }
            else if (key == GlfwKey.C)
            {
                camera.Crouch((float)sceneClock); // flying, C descends
            }
            else if (key == GlfwKey.CapsLock)
            {
                camera.ToggleSlow();
            }
            else if ((int)key == '0')
            {
                film.Play();
            }
            else if (key == GlfwKey.F1)
            {
                keysShown = !keysShown;
            }
            else if (key == GlfwKey.F10)
            {
                rtxShown = !rtxShown;
            }
            else if (key == GlfwKey.G)
            {
                // The streets of Ottawa from Google's 3D tiles, or from OpenStreetMap again (GoogleStreets).
                googleChosen = !googleChosen;
                if (googleChosen && aerial is null && googleLoading is null) googleTried = false; // a failed fetch is tried once more
                camera.Footprints = googleChosen ? [] : streetFootprints;
                Console.WriteLine(googleChosen
                    ? $"SCENE streets from Google's 3D tiles{(GoogleStreets.Keyed ? "" : ": no GOOGLE_MAPS_API_KEY, OpenStreetMap stays")}"
                    : "SCENE streets from OpenStreetMap");
            }
            else if (key == GlfwKey.H)
            {
                clocks.Shown = !clocks.Shown;
            }
            else if (key == GlfwKey.B)
            {
                hazy = !hazy;
                Console.WriteLine($"SCENE haze {(hazy ? "on" : "off")}");
            }
            else if (key == GlfwKey.R && rays is not null)
            {
                raysOn = !raysOn;
                Console.WriteLine($"SCENE rays {(raysOn ? "traced" : "off: the shadows painted")}");
            }
            else if (key == GlfwKey.F6 && tracer is not null)
            {
                MaxOut(!maxOut);
            }
            else if (key == GlfwKey.F9 && tracer is not null)
            {
                // The path tracer by hand: Max out no longer holds, and F9 acts on what is shown.
                maxOut = false;
                tracer.MaxOut = false;
                pathTracing = !pathTracing;
                tracer.Forget();
                Console.WriteLine($"SCENE path tracing {(pathTracing ? "on" : "off")}");
            }
            else if (key == GlfwKey.F7 && upscaler is not null)
            {
                // DLSS Super Resolution on the raster pass, or the window drawn at its own size as before.
                upscaling = !upscaling;
                upscaler.Forget();
                rtxDrawn = default;
                Console.WriteLine($"SCENE super resolution {(upscaling ? "on" : "off")}");
            }
            else if (key is GlfwKey.Home or GlfwKey.Enter) // back to the first view, from wherever the player got stuck
            {
                camera.Reset();
            }
            else if ((int)key is >= '1' and <= '9' && ((int)mods & ModShift) != 0 && film.PlayView((int)key - '0'))
            {
                landOn = (int)key - '0'; // the view's shot, filmed; the player stands at the view after it
            }
            else if ((int)key is >= '1' and <= '9' && camera.GoTo((int)key - '0')) // a flight, from where the picture is
            {
                // a view, or the drift for 1
            }
            else if (key == GlfwKey.P)
            {
                ShowPairing(!pairingShown);
                invitationShown &= !pairingShown;
                notesShown &= !pairingShown;
            }
            else if (key == GlfwKey.N)
            {
                ShowNotes(!notesShown);
            }
            else if (key == GlfwKey.T)
            {
                RoomCaptions.Toggle();
            }
            else if (key == GlfwKey.L)
            {
                NextLanguage();
            }
            else if (key == GlfwKey.M) // Tab is the wheel's, as in the Observatory
            {
                shelf.Show(at);
            }
            else if (key == GlfwKey.Escape) // never the window: one Esc too many, leaving the game or the shelf, closed it
            {
                pairingShown = false;
                invitationShown = false;
                notesShown = false;
            }
        });
        window.OnKey += onKey;

        // A row under the cursor, in the card's pixels, from the window's coordinates.
        int RowAt(GlfwWindow w)
        {
            if (card is null || !shelf.Open)
            {
                return -1;
            }

            var x = w.CursorX * extent.Width / Math.Max(1, w.Width) - CardMargin();
            var y = w.CursorY * extent.Height / Math.Max(1, w.Height) - CardMargin();
            if (x < 0 || x >= card.Width)
            {
                return -1;
            }

            foreach (var (row, top, bottom) in card.Rows)
            {
                if (y >= top && y < bottom)
                {
                    return row;
                }
            }

            return -1;
        }

        // Whether the cursor is on a card drawn at corner, in the window's pixels.
        bool Over(GlfwWindow w, (int X, int Y) corner, int width, int height)
        {
            var x = w.CursorX * extent.Width / Math.Max(1, w.Width);
            var y = w.CursorY * extent.Height / Math.Max(1, w.Height);
            return x >= corner.X && x < corner.X + width && y >= corner.Y && y < corner.Y + height;
        }

        bool TryModuleClick(double x, double y, bool simulated)
        {
            if (shelf.Open || pairingShown || invitationShown || notesShown || gaiaModule is not { } module || gaiaPick is not { } target) return false;
            var action = SceneModuleInteraction.TryClick(module, target, x, y, simulated);
            if (action is null) return false;
            Console.WriteLine($"MODULE gaia {action} requested by {(simulated ? "simulated-demo-test" : "human-mouse")} click: v{module.Version.ToString(2)}; sha256 {module.CodeHash}");
            return true;
        }

        // A click on a session's robot selects it, the scene's alone, with no command; a second click soon after shows its
        // pane in wmux; a click beside the desks lets the selection go and passes on. The module only names the session.
        bool TryOfficeClick(double x, double y)
        {
            if (shelf.Open || pairingShown || invitationShown || notesShown || officeFailed || officeStage is null || officeFeed is null || officeView is not { } view) return false;
            var now = clock.Elapsed.TotalSeconds;
            if (WidgetPicking.Pick(officeStage.Hits, view, extent.Width, extent.Height, x, y) is not { } session)
            {
                var had = officeSelection.Selected;
                if (officeSelection.Click(null, 0, now) == OfficeSelection.Outcome.Cleared)
                {
                    Console.WriteLine("WIDGETS agent-office selection cleared");
                    wall.Released(had);
                }

                return false;
            }

            OfficeClicked(session);
            return true;
        }

        // The session whose robot is under (x, y), when a press there may start turning its ring; null in the flat view,
        // whose rows do not turn, and whenever a click on the office would not reach it.
        string? OfficeRobotAt(double x, double y) =>
            shelf.Open || pairingShown || invitationShown || notesShown || officeFailed || quality == Quality.Flat
            || officeStage is null || officeFeed is null || officeView is not { } view
                ? null
                : WidgetPicking.Pick(officeStage.Hits, view, extent.Width, extent.Height, x, y);

        // A click on the lectern's book: its outline, its arrows or its links (LearnReader).
        bool TryReaderClick(double x, double y)
        {
            if (shelf.Open || pairingShown || invitationShown || notesShown || officeView is not { } view || hallGallery.Book is not { } book) return false;
            if (HallGallery.OnRect(view, book, HallGallery.BookRect, extent.Width, extent.Height, x, y) is not { } at) return false;
            reader.Click(at.U, at.V);
            return true;
        }

        // A click on a course's book or a Streeling drawer opens it on the lectern, and the camera goes to read it; one on
        // a demo or an artifact opens its page in the browser.
        bool TryGalleryClick(double x, double y)
        {
            if (shelf.Open || pairingShown || invitationShown || notesShown || officeView is not { } view) return false;
            if (hallGallery.Pick(view, extent.Width, extent.Height, x, y) is not { } action) return false;
            if (action.StartsWith("open:", StringComparison.Ordinal))
            {
                HallGallery.Browse(action[5..]);
            }
            else if (ReadOnLectern(action))
            {
                camera.GoTo(FreeCamera.Views.Length + 1);
            }

            return true;
        }

        // Opens "course:N" or "department:N" of the gallery on the lectern; false for anything else.
        bool ReadOnLectern(string action)
        {
            if (hallGallery.Shelved is not { } content) return false;
            var (kind, what) = action.IndexOf(':') is var colon and > 0 ? (action[..colon], action[(colon + 1)..]) : (action, "");
            if (kind == "course" && int.TryParse(what, out var c) && c >= 0 && c < content.Courses.Length)
            {
                Console.WriteLine($"GALLERY reads {content.Courses[c].Url} on the lectern");
                reader.OpenCourse(content.Courses[c].Title, content.Courses[c].Url);
                return true;
            }

            if (kind == "department" && int.TryParse(what, out var d) && d >= 0 && d < content.Departments.Count)
            {
                var (name, modules) = content.Departments[d];
                Console.WriteLine($"GALLERY reads Streeling's {name} on the lectern");
                reader.OpenDepartment(name, [.. modules.Select(m => new Delta.SceneModules.ReaderItem(m.Title, m.Url))]);
                return true;
            }

            return false;
        }

        // A click on a session's robot, picked by the office.
        void OfficeClicked(string session)
        {
            if (officeFeed is null) return;
            var now = clock.Elapsed.TotalSeconds;
            var clicked = officeFeed.Sessions.FirstOrDefault(s => s.Id == session);
            // A session that waits for the user (the « ? »): the shelf shows what it asks and answers it (SessionQuestion).
            if (clicked is { Activity: Delta.SceneModules.OfficeActivity.Waiting } && officeFeed.Surface(session) is { } asking)
            {
                Console.WriteLine($"WIDGETS agent-office shows session {session}'s question");
                shelf.Show(now, $"« ? » {clicked.Name}", () => SessionQuestion.Rows(session, asking, () => shelf.Hide()));
                return;
            }

            var started = clicked?.Started ?? 0;
            var outcome = officeSelection.Click(session, started, now);
            if (outcome != OfficeSelection.Outcome.Open)
            {
                Console.WriteLine($"WIDGETS agent-office {(outcome == OfficeSelection.Outcome.Selected ? "selected" : "let go of")} session {session}");
                if (outcome == OfficeSelection.Outcome.Selected) ShowOnWall(WallSource.Kind.Session, session);
                else wall.Released(session);
                return;
            }

            var surface = officeFeed.Surface(session);
            Console.WriteLine($"WIDGETS agent-office double click on session {session}: {(surface is null ? "no wmux pane" : "showing its pane")}");
            if (surface is not null)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Shelf.Wmux.Focus(surface); }
                    catch (Exception e) { Console.Error.WriteLine($"WIDGETS agent-office could not show the pane ({e.GetType().Name}: {e.Message})"); }
                });
            }
        }

        // A click on the arcade cabinet walks up to it and starts its game; the keys are the game's until Esc.
        bool TryCabinetClick(double x, double y, bool simulated = false)
        {
            if (shelf.Open || pairingShown || invitationShown || notesShown || officeView is not { } view || !cabinet.Hit(view, extent.Width, extent.Height, x, y)) return false;
            ShowOnWall(WallSource.Kind.Parsec, null);
            if (!simulated) PlayArcade();
            return true;
        }

        // A click on Delta's chest screen, where Pong runs: the wall shows Pong. The screen's rectangle in the torso's
        // frame is ScreenGpuRenderer's for a module frame, at its depth.
        bool TryChestClick(double x, double y)
        {
            if (shelf.Open || pairingShown || invitationShown || notesShown || arcade is null || pongWall is null || chestView is not { } chest) return false;
            var corners = new[] { new SN.Vector3(-0.15f, 0.63f, 0.218f), new(0.15f, 0.63f, 0.218f), new(-0.15f, 0.45f, 0.218f), new(0.15f, 0.45f, 0.218f) }
                .Select(p => OnScreen(p, chest)).ToList();
            if (corners.Any(c => c is null)) return false;
            var (xs, ys) = (corners.Select(c => c!.Value.X).ToList(), corners.Select(c => c!.Value.Y).ToList());
            if (x < xs.Min() || x > xs.Max() || y < ys.Min() || y > ys.Max()) return false;
            ShowOnWall(WallSource.Kind.Pong, null);
            return true;
        }

        // Once a frame while the wall shows a session: its transcript read again every 3 s on the thread pool (the shelf's
        // reader, its ends only), its card drawn again when a line changed, checked once a second.
        void RefreshWallCard(string id)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (now - wallReadAt >= 3.0 && Interlocked.CompareExchange(ref wallReading, 1, 0) == 0)
            {
                wallReadAt = now;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var projects = Path.Combine(Shelf.ClaudeDir(), "projects");
                        var path = Directory.Exists(projects) ? Directory.EnumerateFiles(projects, id + ".jsonl", SearchOption.AllDirectories).FirstOrDefault() : null;
                        var ends = path is null ? null : Conversations.Read(path, Conversations.Search);
                        wallTranscript = ends is null ? null : new(ends.Title, ends.Requests, File.GetLastWriteTimeUtc(path!), ends.Unfound);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { wallTranscript = null; }
                    finally { Volatile.Write(ref wallReading, 0); }
                });
            }

            if (now - wallCardAt < 1.0) return;
            wallCardAt = now;
            var lines = WallSource.Card(id, officeFeed?.Sessions ?? [], officeFeed?.ReadAgo(DateTimeOffset.UtcNow), wallTranscript, DateTimeOffset.UtcNow, Words.Say);
            if (lines.SequenceEqual(wallLines)) return;
            wallLines = lines;
            wallPixels = WallCard.Render(lines, Words.Say("The cabinet: Parsec   Delta's chest: Pong", "Le cabinet : Parsec   La poitrine de Delta : Pong"));
            wallCardRevision++;
        }

        void ShowOnWall(WallSource.Kind kind, string? session)
        {
            var before = (wall.Showing, wall.Session);
            if (kind == WallSource.Kind.Session && session is not null) wall.ShowSession(session);
            else if (kind == WallSource.Kind.Pong) wall.ShowPong();
            else wall.ShowParsec();
            if (before != (wall.Showing, wall.Session))
            {
                (wallTranscript, wallCardAt) = (null, double.NegativeInfinity);
                Console.WriteLine($"WALL shows {wall.Showing}{(wall.Session is { } s ? " " + s : "")}");
            }
        }

        // A click in the window, at x, y in the swapchain's pixels: the first of Gaia's module, the chest, the office and
        // the cabinet that it falls on takes it.
        void Click(double x, double y, bool simulated = false)
        {
            if (!TryModuleClick(x, y, simulated) && !TryChestClick(x, y) && !TryOfficeClick(x, y) && !TryReaderClick(x, y) && !TryGalleryClick(x, y)
                && !TryCabinetClick(x, y, simulated)) TryDoorClick(x, y);
        }

        // A click on the roll-up door opens it onto the streets of Ottawa, or closes it; the first opening fetches the
        // streets (OttawaStreets), or reads them where they were kept.
        bool TryDoorClick(double x, double y)
        {
            if (shelf.Open || pairingShown || invitationShown || notesShown || officeView is not { } view) return false;
            if (HallGallery.OnRect(view, StreetDoor.Frame, StreetDoor.Rect, extent.Width, extent.Height, x, y) is null) return false;
            streetDoor.Toggle();
            Console.WriteLine($"DOOR {(streetDoor.Open ? "opens" : "closes")}");
            if (streetDoor.Open && streetsLoading is null) streetsLoading = OttawaStreets.Load();

            return true;
        }

        // --wall-real, once a frame after the splash: once the office's reading names the session and its robot is drawn
        // (4 s at most), a click on a point of that robot that the window's picking gives to it, through Click; then at
        // 6.5 s its card printed and checked: shown, its ID, a fresh reading, its transcript found.
        void WallReal(string id, double at)
        {
            void Check(bool ok, string what) { Console.WriteLine($"WALLREAL {(ok ? "ok" : "FAIL")} {what}"); if (!ok) wallTestFailures++; }
            if (wallTestStep == 0 && officeFeed?.ReadAgo(DateTimeOffset.UtcNow) is not null && officeFeed.Sessions.Any(s => s.Id == id)
                && (officeStage?.Hits.Any(h => h.Click == id) == true || at >= 4.0))
            {
                wallTestStep = 1;
                var robot = officeStage?.Hits.Where(h => h.Click == id).ToList() ?? [];
                var point = officeView is { } view
                    ? robot.Select(h => OnScreen((h.Min + h.Max) / 2f, view))
                        .FirstOrDefault(c => c is { } p && WidgetPicking.Pick(officeStage!.Hits, view, extent.Width, extent.Height, p.X, p.Y) == id)
                    : null;
                var tree = officeStage?.Problems is [{ } problem, ..] ? $"its tree refused: {problem}" : "its tree accepted";
                Check(point is not null, $"session {id[..Math.Min(8, id.Length)]} has a robot the window can click ({robot.Count} instances, {officeFeed.Sessions.Count} sessions read, {tree})");
                if (point is { } c) Click(c.X, c.Y, true);
                Check(officeSelection.Selected == id, $"a click on its robot selects its session (selected: {officeSelection.Selected ?? "none"})");
            }
            else if (wallTestStep == 1 && at >= 6.5)
            {
                wallTestStep = 2;
                foreach (var line in wallLines) Console.WriteLine($"WALLREAL card: {line}");
                var text = string.Join("\n", wallLines);
                Check(wall.Showing == WallSource.Kind.Session && wall.Session == id, "the wall shows the picked session");
                Check(wallLines.Count > 1 && wallLines[1].StartsWith(id[..Math.Min(8, id.Length)], StringComparison.Ordinal), "its card carries its ID");
                Check(text.Contains(Words.Say("read ", "lue il y a "), StringComparison.Ordinal), $"the office's reading is fresh ({officeFeed?.ReadAgo(DateTimeOffset.UtcNow):F1} s)");
                Check(!text.Contains(Words.Say("no transcript found", "aucun transcript trouvé"), StringComparison.Ordinal), "its transcript was found and read");
            }
        }

        // Where a point in a frame falls in the window's pixels, null behind the eye.
        (double X, double Y)? OnScreen(SN.Vector3 p, SN.Matrix4x4 frameViewProjection)
        {
            var c = SN.Vector4.Transform(new SN.Vector4(p, 1f), frameViewProjection);
            return float.IsFinite(c.W) && c.W > 0 ? (((c.X / c.W) + 1f) * extent.Width * 0.5, ((c.Y / c.W) + 1f) * extent.Height * 0.5) : null;
        }

        // --walk-test, once a frame after the splash: a view's key every 4.2 s from 1 s, checked at its view 2.8 s later,
        // then W and D held a second with the view turned down; at 18 s the drift, W into Delta, then W and D past it.
        void WalkTest(double at, float time)
        {
            void Check(bool ok, string what) { Console.WriteLine($"WALKTEST {(ok ? "ok" : "FAIL")} {what}"); walkChecks++; if (!ok) walkFailures++; }
            void Press(GlfwKey key) { onKey(window, key, 0, GlfwKeyAction.Press, default); onKey(window, key, 0, GlfwKeyAction.Release, default); }
            int[] views = [2, 3, 5, FreeCamera.ArcadeView];
            var eye = camera.View(time).Eye;
            tour.Touch(clock.Elapsed.TotalSeconds);
            var k = walkStep / 3;
            if (k < views.Length)
            {
                var start = 1.0 + (4.2 * k);
                if (walkStep % 3 == 0 && at >= start)
                {
                    Press((GlfwKey)('0' + views[k]));
                    walkStep++;
                }
                else if (walkStep % 3 == 1 && at >= start + 2.8)
                {
                    var want = FreeCamera.Views[views[k] - 2].Eye;
                    Check(SN.Vector3.Distance(eye, want) < 0.02f, $"key {views[k]} reaches its view ({eye.X:F2}, {eye.Y:F2}, {eye.Z:F2})");
                    camera.Look(time, 0, 300); // the view turned down, as the mouse turns it
                    walkFrom = eye;
                    walkHeld.UnionWith([GlfwKey.W, GlfwKey.D]);
                    walkStep++;
                }
                else if (walkStep % 3 == 2 && at >= start + 3.8)
                {
                    walkHeld.Clear();
                    var moved = new SN.Vector2(eye.X - walkFrom.X, eye.Z - walkFrom.Z).Length();
                    Check(!camera.Flying && MathF.Abs(eye.Y - FreeCamera.WalkHeight) < 0.02f && moved > 0.3f,
                        $"from view {views[k]}, W and D looking down walk on the floor ({(camera.Flying ? "flying" : "walking")}, eye at {eye.Y:F2} m, {moved:F2} m across)");
                    walkStep++;
                }

                return;
            }

            switch (walkStep - (3 * views.Length))
            {
                case 0 when at >= 18.0:
                    Press((GlfwKey)'1');
                    walkStep++;
                    break;
                case 1 when at >= 18.4:
                    walkHeld.Add(GlfwKey.W);
                    walkStep++;
                    break;
                case 2 when at >= 20.9:
                    walkHeld.Clear();
                    Check(!camera.Flying && eye.Z > 0.3f + FreeCamera.Radius - 0.01f && eye.Z < 0.8f && MathF.Abs(eye.X) < 0.3f, $"from the drift, W into Delta stops in front of it (eye at {eye.X:F2}, {eye.Z:F2})");
                    walkStep++;
                    break;
                case 3 when at >= 21.3:
                    walkHeld.UnionWith([GlfwKey.W, GlfwKey.D]);
                    walkStep++;
                    break;
                case 4 when at >= 22.8:
                    walkHeld.Clear();
                    Check(eye.X > 0.42f + FreeCamera.Radius && eye.Z > 0.3f + FreeCamera.Radius - 0.01f, $"W and D slide along the fronts of Delta and of Gaia's placard beside it, into neither (eye at {eye.X:F2}, {eye.Z:F2})");
                    walkStep++;
                    break;
            }
        }

        // --wall-test, once a frame after the splash: a robot at 2 s, the chest at 4 s, the cabinet at 6 s, each through
        // the window's own handlers, checked against what the wall then shows.
        void WallTest(double at)
        {
            void Check(bool ok, string what)
            {
                Console.WriteLine($"WALLTEST {(ok ? "ok" : "FAIL")} {what} (wall shows {wall.Showing}{(wall.Session is { } s ? " " + s : "")})");
                if (!ok) wallTestFailures++;
                wallTestStep++;
            }

            if (wallTestStep == 0 && at >= 2.0 && officeFeed?.Sessions is [{ } first, ..])
            {
                OfficeClicked(first.Id);
                Check(wall is { Showing: WallSource.Kind.Session } && wall.Session == first.Id, $"a click on robot {first.Name} shows its session");
            }
            else if (wallTestStep == 1 && at >= 4.0)
            {
                var centre = chestView is { } chest ? OnScreen(new(0f, 0.54f, 0.218f), chest) : null;
                if (centre is { } c) Click(c.X, c.Y, true);
                Check(centre is not null && wall.Showing == WallSource.Kind.Pong, "a click on Delta's chest screen shows Pong");
            }
            else if (wallTestStep == 2 && at >= 6.0)
            {
                var (min, max) = ArcadeCabinet.Bounds;
                var centre = officeView is { } view ? OnScreen((min + max) / 2f, view) : null;
                if (centre is { } c) Click(c.X, c.Y, true);
                Check(centre is not null && wall.Showing == WallSource.Kind.Parsec, "a click on the cabinet brings Parsec back");
            }
        }

        void PlayArcade()
        {
            camera.GoTo(FreeCamera.ArcadeView);
            looking = locked = false;
            Grab(window);
            cabinet.Play();
        }

        // Out of the game, by Esc or a click away from the cabinet: the camera flies back to the hall's first view.
        void LeaveArcade(double at)
        {
            cabinet.Leave();
            robotAt = at + 15.0;
            looking = locked = false;
            Grab(window);
            camera.Reset();
        }

        window.OnMouseButton += (w, button, action, mods) => Guard("a click", () =>
        {
            ctrlAlone = false;
            tour.Touch(clock.Elapsed.TotalSeconds);
            if (film.Playing)
            {
                if (action == GlfwButtonAction.Press)
                {
                    film.Skip(); // a click ends the cinematic
                }

                return;
            }

            // A robot let go: a click if the mouse hardly moved, else the end of turning its ring.
            if (action == GlfwButtonAction.Release && button == 0 && officeDrag is { } dragged)
            {
                officeDrag = null;
                if (officeDragMoved < DragSlop) Click(officeDragFrom.X, officeDragFrom.Y);
                else officeSpin.Release(dragged);
                if (officeDragMoved >= DragSlop) Console.WriteLine($"WIDGETS agent-office turned by hand: session {dragged}, {officeTurns.GetValueOrDefault(dragged):F2} rad in all");
                return;
            }

            // In the game, a left click away from the cabinet leaves it, as Esc does; one on it is the game's.
            if (cabinet.Playing && action == GlfwButtonAction.Press && button == 0 && !locked)
            {
                var (cx, cy) = (w.CursorX * extent.Width / Math.Max(1, w.Width), w.CursorY * extent.Height / Math.Max(1, w.Height));
                if (officeView is not { } seen || !cabinet.Hit(seen, extent.Width, extent.Height, cx, cy))
                {
                    LeaveArcade(clock.Elapsed.TotalSeconds);
                    return;
                }
            }

            if (wheel.Open)
            {
                if (action == GlfwButtonAction.Press && button == 0)
                {
                    if (!locked)
                    {
                        var (inner, _) = ToolWheel.Radii(extent.Width, extent.Height);
                        wheel.Point((float)((w.CursorX * extent.Width / Math.Max(1, w.Width)) - (extent.Width / 2.0)), (float)((w.CursorY * extent.Height / Math.Max(1, w.Height)) - (extent.Height / 2.0)), inner);
                    }

                    wheel.Close(true); // a click picks
                }

                return;
            }

            if ((int)button == 1) // the right button held turns the view, and frees a locked one; the cursor hides meanwhile
            {
                looking = action == GlfwButtonAction.Press && !shelf.Open;
                locked &= !looking;
                Grab(w);
                return;
            }

            if (action == GlfwButtonAction.Press && button == 0 && ((int)mods & ModControl) != 0 && !shelf.Open)
            {
                locked = !locked; // Ctrl and a click: the view follows the mouse until the next one
                Grab(w);
                return;
            }

            if (action == GlfwButtonAction.Press && button == 0 && locked)
            {
                // Locked, a click is at the screen's centre, where the view aims.
                Click(extent.Width / 2.0, extent.Height / 2.0);
                return;
            }

            var icon = Panel.PhoneSize(extent.Height);
            if (action == GlfwButtonAction.Press && (Over(w, PhoneAt(), icon, icon) || (pairingShown && pairingCard is not null && Over(w, PairingAt(), pairingCard.Width, pairingCard.Height))))
            {
                ShowPairing(!pairingShown);
                invitationShown &= !pairingShown;
                notesShown &= !pairingShown;
            }
            else if (action == GlfwButtonAction.Press && notesShown && notesCard is not null && Over(w, NotesAt(), notesCard.Width, notesCard.Height))
            {
                notesShown = false;
            }
            else if (action == GlfwButtonAction.Press && versionLabel is not null && Over(w, VersionAt(), versionLabel.Width, versionLabel.Height))
            {
                ShowNotes(!notesShown);
            }
            else if (action == GlfwButtonAction.Press && invitationShown && invitationCard is not null && Over(w, InvitationAt(), invitationCard.Width, invitationCard.Height))
            {
                invitationShown = false;
            }
            else if (action == GlfwButtonAction.Press && guestBadge is not null && link.Read(clock.Elapsed.TotalSeconds) is { Guests: > 0 } now && Over(w, GuestAt(), guestBadge.Width, guestBadge.Height))
            {
                link.Tell($$"""{"guests_mic":{{(now.GuestsHear ? "false" : "true")}}}""");
            }
            else if (action == GlfwButtonAction.Press && inviteButton is not null && Over(w, InviteAt(), inviteButton.Width, inviteButton.Height))
            {
                link.Tell("""{"invite":true}""");
            }
            else if (action == GlfwButtonAction.Press && languageButton is not null && Over(w, LanguageAt(), languageButton.Width, languageButton.Height))
            {
                NextLanguage();
            }
            else if (action == GlfwButtonAction.Press && micButton is not null && Over(w, MicAt(), micButton.Width, micButton.Height))
            {
                ToggleMic(micDrawn.Off);
            }
            else if (action == GlfwButtonAction.Press && RowAt(w) is var row and >= 0)
            {
                var at = clock.Elapsed.TotalSeconds;
                shelf.Select(at, row);
                shelf.Enter(at);
            }
            else if (action == GlfwButtonAction.Press && button == 0)
            {
                var (cx, cy) = (w.CursorX * extent.Width / Math.Max(1, w.Width), w.CursorY * extent.Height / Math.Max(1, w.Height));
                if (OfficeRobotAt(cx, cy) is { } robot)
                {
                    // Held: the release decides between a click and a turn (the frame loop turns it meanwhile).
                    (officeDrag, officeDragFrom, officeDragAt, officeDragMoved) = (robot, (cx, cy), (cx, cy), 0);
                    officeSpin.Take(robot);
                }
                else
                {
                    Click(cx, cy);
                }
            }
        });
        window.OnScroll += (w, offset) => Guard("a scroll", () =>
        {
            tour.Touch(clock.Elapsed.TotalSeconds);
            if (shelf.Open && offset.Y != 0)
            {
                shelf.Move(clock.Elapsed.TotalSeconds, offset.Y > 0 ? -1 : 1);
            }
            else if (offset.Y != 0)
            {
                camera.Wheel(offset.Y);
            }
        });

        Console.WriteLine($"SCENE present mode {presentMode}, {images.Length} swapchain images, {extent.Width}x{extent.Height}{(cap > 0 ? $", capped at {cap} fps" : "")}{(paceByScreen ? ", each frame after the last is on screen" : "")}");
        if (cap > 0 && OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1); // 1 ms sleeps, so the cap paces frames without spinning the whole time
        }

        var robot = new Robot();
        var worlds = new SN.Matrix4x4[Robot.Parts];
        var glows = new SN.Vector4[Robot.Parts];
        var player = new Robot(); // the film's robot at the cabinet
        var playerWorlds = new SN.Matrix4x4[Robot.Parts];
        var playerGlows = new SN.Vector4[Robot.Parts];
        Process? encoder = null; // FFmpeg, fed the film's frames
        var filmFrames = 0;
        var filmSound = new List<float>(); // the film's sound, the game's, at 44.1 kHz mono
        var period = cap > 0 ? 1.0 / cap : 0.0;
        var screenSeen = double.NegativeInfinity; // when paceToScreen last read the window's screen
        var nextFrame = 0.0;
        var lastPresent = 0.0;
        var frameTimes = new FrameStats();
        var screenTimes = new FrameStats(); // paceByScreen: from reading the keys to the picture on screen, ms
        long presentCount = 0; // the presents' ids, rising across swapchains
        nint waitFor = 0; // the last frame presented, which the next waits for on screen; 0 for none
        var waitForInput = 0.0; // when that frame read the keys
        // paceByScreen, one frame ahead: when frames drawn one after another miss the refresh (keys to submit plus the GPU's
        // time beyond 90% of it, 5 of the last 30), each frame waits for the one before the last to be on screen, so that
        // it is drawn while the last waits for the screen: a refresh more of latency instead of half the frames. Back to
        // none ahead when 90 frames in a row take under 70% of the refresh. At 3840x2054 with the rays and 4x MSAA a frame
        // alone took more than a refresh at 60 Hz and the window showed 30 fps (2026-11-15).
        var ahead = false;
        var aheadFrames = 0;
        nint lastPresented = 0;
        var lastInput = 0.0;
        var refreshMs = 1000.0 / 60;
        var refreshSeen = double.NegativeInfinity;
        var work = new Queue<double>();
        var gpuTimes = new FrameStats();

        void Pace(double ms)
        {
            work.Enqueue(ms);
            if (work.Count > 90)
            {
                work.Dequeue();
            }

            if (!paceByScreen)
            {
                return;
            }

            if (!ahead && work.Count >= 30 && work.Skip(work.Count - 30).Count(w => w > 0.9 * refreshMs) >= 5)
            {
                ahead = true;
                Console.WriteLine($"SCENE pace: one frame ahead, frames take {work.Last():F1} ms against a refresh of {refreshMs:F1} ms");
            }
            else if (ahead && work.Count == 90 && work.All(w => w < 0.7 * refreshMs))
            {
                ahead = false;
                Console.WriteLine($"SCENE pace: none ahead again, frames take {work.Last():F1} ms against a refresh of {refreshMs:F1} ms");
            }
        }
        var frame = 0;
        var generationHeld = 0; // frames left before frame generation may turn on again
        var generationStatus = uint.MaxValue; // DLSS-G's status as last printed
        var hudlessFailed = false; // the HUD-less colour's tag failed once, and said so
        var hudlessTagged = false; // it was tagged once, and said so
        var shot = 0;
        var last = filmDir is null ? clock.Elapsed.TotalSeconds : 0.0;
        var firstFrame = -1.0;
        // When the memory climbs, minimised or not, timed like the other lines from the first frame.
        using var memory = MemoryWatch.Start(() => firstFrame < 0 ? 0 : clock.Elapsed.TotalSeconds - firstFrame, MemoryWatch.Read, Console.WriteLine);
        var clears = stackalloc VkClearValue[2];
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var loopStart = clock.Elapsed;
        // While a hand holds the window's title bar or border, Windows keeps PollEvents in its own loop until it lets go,
        // moving or not (a frame of 2744 ms on 2026-10-31, as the window came back from maximised; 1.5 s of a 1.5 s hold in
        // --modal-drill). A timer's ticks and the window's move, size and refresh callbacks, all called from inside that
        // loop, then draw the frames, once the pump has held the loop for a frame's time, so the hall goes on moving.
        var polling = false; // inside PollEvents
        var drawingAside = false; // a frame drawn from inside the pump, which draws no other
        var pumpedFrom = 0.0; // when the event pump started, or a frame drawn inside it ended
        var heldFrom = 0.0; // when the event pump started, or a frame drawn inside it started: the frames' pace
        var drawnAside = 0;
        void DrawAside()
        {
            if (polling && !drawingAside && clock.Elapsed.TotalSeconds - heldFrom >= 0.015)
            {
                drawingAside = true;
                drawnAside++;
                heldFrom = clock.Elapsed.TotalSeconds;
                Guard("a frame while the window is held", () => Frame(false));
                pumpedFrom = clock.Elapsed.TotalSeconds; // the pump's rest belongs to the next frame
                drawingAside = false;
            }
        }

        window.OnPosition += (_, _) => DrawAside();
        window.OnSize += (_, _) => DrawAside();
        window.OnRefresh += _ => DrawAside();
        using var pumpTimer = new PumpTimer(OperatingSystem.IsWindows() ? ModalDrill.Own() : 0, 10, DrawAside);
        while (!window.ShouldClose && Frame(true))
        {
        }

        if (drawnAside > 0)
        {
            Console.WriteLine($"SCENE frames drawn while the window was held: {drawnAside}");
        }

        // One frame, its keys and mouse read when poll; false when the loop ends.
        bool Frame(bool poll)
        {
            // The GPU done with this slot's last frame before the keys and the mouse are read, not after: the picture
            // then shows the input it was drawn from without a frame's wait in between.
            device.WaitForFences([fences[frame % FramesInFlight]], true, -1);
            if (waitFor != 0)
            {
                // The last frame on screen before the keys and the mouse are read (paceByScreen); a tenth of a second
                // at most, should the screen stop answering.
                VkResult onScreen;
                try
                {
                    onScreen = device.WaitForPresentKHR(swapchain, waitFor, 100_000_000);
                }
                catch (VkResultError e)
                {
                    onScreen = e.Result;
                }

                if (onScreen == VkResult.Success)
                {
                    screenTimes.Add((clock.Elapsed.TotalSeconds - waitForInput) * 1000.0);
                }

                waitFor = 0;
            }

            if (reflex is not null && hasSwapchain)
            {
                // Reflex holds the frame back here, then is told where it starts and where its input is read.
                reflex.Sleep(swapchain);
                reflex.Mark(swapchain, presentCount + 1, VkLatencyMarkerNV.SimulationStartNV);
                reflex.Mark(swapchain, presentCount + 1, VkLatencyMarkerNV.InputSampleNV);
            }
            else if (frameGeneration && hasSwapchain)
            {
                // Frame generation: the frame's token, Reflex's sleep through Streamline and the simulation's start.
                Streamline.BeginFrame();
            }

            if (poll)
            {
                pumpedFrom = heldFrom = clock.Elapsed.TotalSeconds;
                polling = true;
                Glfw.PollEvents();
                polling = false;
            }

            var inputAt = clock.Elapsed.TotalSeconds;
            gap.Pumped(inputAt - pumpedFrom); // a frame drawn aside counts the pump's time since the last one
            if (poll)
            {
                placement.Apply(window, tray); // a minimise or a move asked for waits until the hand lets go
            }
            var now = filmDir is null ? clock.Elapsed.TotalSeconds : frame / (double)FilmFps; // a film's time is its frames'
            if (paceByScreen && now >= refreshSeen + 2.0)
            {
                refreshSeen = now;
                var hz = Placement.MonitorOf(window).VideoMode.RefreshRate;
                refreshMs = hz > 0 ? 1000.0 / hz : refreshMs;
            }

            if (paceToScreen && now >= screenSeen + 2.0)
            {
                screenSeen = now;
                var refresh = Placement.MonitorOf(window).VideoMode.RefreshRate;
                if (2 * refresh != cap && refresh > 0)
                {
                    cap = 2 * refresh;
                    period = 1.0 / cap;
                    Console.WriteLine($"SCENE paced at {cap} fps, twice the window's screen");
                }
            }

            if (now >= limit)
            {
                reason = "time limit";
                return false;
            }

            var dt = (float)(now - last);
            last = now;
            // The scene's time goes at a quarter while the wheel is open; the camera, the cinematic and the wheel itself
            // keep the clock's.
            var sceneDt = dt * wheel.TimeScale;
            sceneClock += sceneDt;
            wheel.Advance(dt);
            film.Advance(dt);
            arcadeFilm?.Advance(dt);
            origamiFilm?.Advance(dt);
            if (landOn != 0 && (film.Filming || !film.Playing))
            {
                // Behind the black, so the shot hands the view back where it filmed; a skip before the black stays put.
                if (film.Filming)
                {
                    camera.GoTo(landOn, cut: true);
                }

                landOn = 0;
            }

            arcade?.Advance(sceneDt);
            cabinet.Advance(sceneDt);
            // The splash is timed from the first frame on screen, not from start-up, which takes about a second; --demo's
            // timeline and --shots start with it, while the brain's lines are timed by the link's own clock.
            var shown = firstFrame < 0 ? 0.0 : now - firstFrame;
            var splashing = shown < S;
            var linkNow = demo ? Math.Max(0.0, shown - S) : now;
            if (demo)
            {
                link.Demo(linkNow);
            }

            var moment = link.Read(linkNow);
            if (demo)
            {
                pairingShown = linkNow % Link.DemoSeconds >= 20.0; // the demo opens the pairing card for its last second
            }
            var time = (float)sceneClock;
            shelf.Tick(now);
            robot.Presenting = shelf.Open;
            robot.Pose(time, sceneDt, moment, worlds, glows);
            // The small robot takes the cabinet while nobody plays; its game, silent, is on the wall too.
            if (robotPlays && !splashing && now >= robotAt && !cabinet.Playing && !cabinet.RobotPlaying)
            {
                robotAt = now + 60.0; // again a minute after its game ends or could not start
                cabinet.PlayRobot(ArcadeFilm.Pilot());
            }

            var playerShown = arcadeFilm is not null || cabinet.RobotPlaying;
            if (playerShown)
            {
                player.Pose(time, sceneDt, moment, playerWorlds, playerGlows);
                arcadePlayer.Pose(sceneDt, cabinet.Holding, playerWorlds);
            }

            if (touring)
            {
                tour.Advance(now, !splashing && !shelf.Open && !film.Playing && !cabinet.Playing && !wheel.Open && !pairingShown && !invitationShown && !notesShown);
            }

            // Captures: the cinematic, a view's shot, the wheel or a view's flight, once the splash is over.
            if (!splashing && filmAtStart)
            {
                filmAtStart = false;
                film.Play(filmFrom);
            }

            if (!splashing && filmView != 0)
            {
                landOn = film.PlayView(filmView) ? filmView : 0;
                filmView = 0;
            }

            if (!splashing && arcadeAtStart)
            {
                arcadeAtStart = false;
                PlayArcade();
            }

            // The arcade's film: its game, played by its pilot, then the film from the game's first frame to its end.
            if (!splashing && arcadeFilm is not null && arcadeFilm.Time < 0f)
            {
                if (!filmAsked)
                {
                    filmAsked = true;
                    cabinet.PlayFilmed(ArcadeFilm.Pilot());
                }
                else if (cabinet.Frames > 0)
                {
                    arcadeFilm.Start();
                }
                else if (!cabinet.Playing)
                {
                    reason = "film: the game did not start";
                    return false;
                }
            }

            if (arcadeFilm is { Done: true } || origamiFilm is { Done: true })
            {
                reason = "film done";
                return false;
            }

            // The origami table's film: rolls once the splash is over, the table told to play its steps from the first.
            if (!splashing && origamiFilm is { Time: < 0f })
            {
                if (office is null)
                {
                    reason = "film: no office module, no origami table";
                    return false;
                }

                origamiFilm.Start();
            }

            if (!splashing && wheelAtStart)
            {
                wheelAtStart = false;
                wheel.TabDown(now);
                wheel.TabUp(now);
            }

            if (!splashing && resizeTo is { } resized && shown - S >= 1.0)
            {
                window.Size = resized;
                resizeTo = null;
            }

            if (!splashing && modalDrill)
            {
                modalDrill = false;
                ModalDrill.Start(what => Console.WriteLine($"DRILL {what} at {clock.Elapsed.TotalSeconds - firstFrame:F2} s"), (w, h) => resizeTo = new int2(w, h));
            }

            if (!splashing && flyTo != 0 && shown - S >= 0.5)
            {
                camera.GoTo(flyTo);
                flyTo = 0;
            }

            // The camera drifts in front of the robot until it is moved (FreeCamera); the keys are read while the shelf,
            // which takes the arrows, is closed, and not while the cinematic films.
            if ((looking || locked) && !window.IsFocused)
            {
                looking = false; // the window lost focus with the view held: give the cursor back
                locked = false;
                window.SetInputMode(GlfwInputMode.Cursor, CursorNormal);
            }

            if (looking || locked)
            {
                var (cx, cy) = (window.CursorX, window.CursorY);
                var (inner, outer) = ToolWheel.Radii(extent.Width, extent.Height);
                if (wheel.Open)
                {
                    wheel.Aim((float)(cx - lookFrom.X), (float)(cy - lookFrom.Y), inner, outer);
                }
                else if (!film.Playing)
                {
                    camera.Look(time, cx - lookFrom.X, cy - lookFrom.Y);
                    if (cx != lookFrom.X || cy != lookFrom.Y)
                    {
                        tour.Touch(now);
                    }
                }

                lookFrom = (cx, cy);
            }
            else if (wheel.Open)
            {
                var (inner, _) = ToolWheel.Radii(extent.Width, extent.Height);
                wheel.Point((float)((window.CursorX * extent.Width / Math.Max(1, window.Width)) - (extent.Width / 2.0)),
                    (float)((window.CursorY * extent.Height / Math.Max(1, window.Height)) - (extent.Height / 2.0)), inner);
            }

            // A robot held: its ring follows the mouse sideways once it has moved past the slop. A window that loses
            // focus with the button held lets the robot go, as it gives the cursor back.
            // Let go, it coasts on (OfficeSpin).
            var spinDt = double.IsNaN(officeSpinAt) ? 0 : Math.Clamp(now - officeSpinAt, 0, 0.25);
            officeSpinAt = now;
            if (officeDrag is { } turning)
            {
                var (dx, dy) = (window.CursorX * extent.Width / Math.Max(1, window.Width), window.CursorY * extent.Height / Math.Max(1, window.Height));
                officeDragMoved = Math.Max(officeDragMoved, Math.Abs(dx - officeDragFrom.X) + Math.Abs(dy - officeDragFrom.Y));
                var turned = 0.0;
                if (officeDragMoved >= DragSlop && dx != officeDragAt.X)
                {
                    turned = (dx - officeDragAt.X) / Math.Max(1, extent.Width) * TurnPerWidth;
                    officeTurns[turning] = officeTurns.GetValueOrDefault(turning) + turned;
                    tour.Touch(now);
                }

                officeSpin.Follow(turned, spinDt);
                officeDragAt = (dx, dy);
                if (!window.IsFocused || wheel.Open || shelf.Open)
                {
                    officeDrag = null;
                    officeSpin.Release(turning);
                }
            }

            officeSpin.Step(officeTurns, spinDt);

            if (!shelf.Open && !film.Playing && !cabinet.Playing && (window.IsFocused || walkHeld.Count > 0))
            {
                bool Held(GlfwKey key) => window.GetKey(key) == GlfwKeyAction.Press || walkHeld.Contains(key);
                float Axis(GlfwKey a, GlfwKey b, GlfwKey back, GlfwKey back2) => (Held(a) || Held(b) ? 1f : 0f) - (Held(back) || Held(back2) ? 1f : 0f);
                var alt = Held(GlfwKey.LeftAlt) || Held(GlfwKey.RightAlt);
                camera.Move(time, new(Axis(GlfwKey.W, GlfwKey.Up, GlfwKey.S, GlfwKey.Down), Axis(GlfwKey.D, GlfwKey.Right, GlfwKey.A, GlfwKey.Left),
                    Axis(GlfwKey.Space, GlfwKey.E, GlfwKey.C, GlfwKey.C), Held(GlfwKey.LeftShift) || Held(GlfwKey.RightShift),
                    camera.Flying || alt ? 0f : Axis(GlfwKey.E, GlfwKey.E, GlfwKey.Q, GlfwKey.Q), alt), dt);
            }
            else
            {
                camera.Move(time, default, dt); // a flight, a jump or a slide goes on
            }

            // The roll-up door rolls; once the streets are fetched they are drawn behind it, and the camera may go out.
            streetDoor.Step(dt);
            camera.DoorOpen = streetDoor.Passing || camera.Outdoors;
            if (streets is null && streetsLoading is { IsCompleted: true } loaded)
            {
                if (loaded.IsCompletedSuccessfully && loaded.Result is { } city)
                {
                    streets = new Mesh(device, physical, city.Vertices);
                    streetsPlacement = city.Placement;
                    streetFootprints = [.. city.Footprints.Select(FreeCamera.Footprint.Of)];
                    camera.Footprints = googleChosen ? [] : streetFootprints;
                    Console.WriteLine($"DOOR streets of Ottawa: {city.Buildings} buildings, {city.Streets} streets, {city.Left} left out where the hall stands; {OttawaStreets.Attribution}");
                }
                else
                {
                    Console.Error.WriteLine($"DOOR streets of Ottawa not fetched: {loaded.Exception?.InnerException?.Message ?? "no answer"}");
                    streetsLoading = null; // the next opening tries again
                }
            }

            // Google's tiles, chosen once the door's place is known: fetched once a run (G again tries once more after a
            // failure), then drawn in place of OpenStreetMap's buildings.
            if (googleChosen && aerial is null && googleLoading is null && !googleTried && streetsPlacement is { } placed)
            {
                googleTried = true;
                Console.WriteLine($"GOOGLE fetching the 3D tiles around the door, at most {GoogleStreets.MostRequests} requests");
                googleLoading = GoogleStreets.Load(placed, line => Console.WriteLine($"GOOGLE {line}"));
            }

            if (googleLoading is { IsCompleted: true } fetched)
            {
                googleLoading = null;
                if (fetched.IsCompletedSuccessfully && fetched.Result is { Vertices.Length: > 0 } tiles)
                {
                    aerial = new Mesh(device, physical, tiles.Vertices, resident: true);
                    if (tiles.Layers.Length > 0) UploadAerial(tiles.Layers);
                    aerialProviders = tiles.Attributions;
                    aerialCardSize = (0, 0);
                    Console.WriteLine($"GOOGLE {tiles.Meshes} tiles, {tiles.Vertices.Length / 3} triangles, {tiles.Layers.Length} images, {tiles.Requests} requests{(tiles.Truncated ? ", stopped at the cap of tiles" : "")}; "
                        + $"the ground {tiles.Ground:F1} m over the ellipsoid; Google Maps: {string.Join("; ", tiles.Attributions)}");
                }
                else
                {
                    Console.Error.WriteLine($"GOOGLE no tiles drawn: {fetched.Exception?.InnerException?.Message ?? "none fetched"}; OpenStreetMap's buildings stay");
                }
            }

            var outside = streetDoor.Openness > 0f || camera.Outdoors; // the streets in view: far away, the haze thinner
            var aspect = extent.Width / (float)extent.Height;
            var pose = film.Pose(aspect) ?? camera.View(time);
            var eye = pose.Eye;
            var projection = SN.Matrix4x4.CreatePerspectiveFieldOfView(pose.Fov * MathF.PI / 180f, aspect, 0.1f, outside ? 350f : 40f);
            projection.M22 = -projection.M22; // Vulkan's clip space has y pointing down
            var viewProj = SN.Matrix4x4.CreateLookAt(eye, pose.Target, pose.Up) * projection;
            gaiaPick = !splashing && gaiaModule is not null ? SceneModulePicking.VersionRect(viewProj, extent.Width, extent.Height) : null;
            officeView = splashing ? null : viewProj;
            if (moduleClickTest && !moduleTestClicked && linkNow >= 2.0 && gaiaPick is { } testTarget)
                moduleTestClicked = TryModuleClick((testTarget.Left + testTarget.Right) / 2, (testTarget.Top + testTarget.Bottom) / 2, true);
            if (wallTest && !splashing) WallTest(linkNow);
            if (wallReal is not null && !splashing) WallReal(wallReal, shown - S);
            if (walkTest && !splashing) WalkTest(shown - S, time);

            // A window hidden by the clock or minimised draws nothing; one whose size changed gets a new swapchain first.
            if (placement.Hidden || window.IsIconified)
            {
                var sleeping = clock.Elapsed.TotalSeconds;
                Thread.Sleep(50);
                gap.Slept(clock.Elapsed.TotalSeconds - sleeping);
                return true;
            }

            if (stale)
            {
                var rebuilding = Stopwatch.StartNew();
                lastPresented = 0;
                if (frameGeneration)
                {
                    // DLSS-G off before the swapchain changes, and for a few frames after (as the Godot fork holds it).
                    Streamline.Generate(false, images.Length, extent.Width, extent.Height, 0, 0);
                    generationHeld = 10;
                    Streamline.DeviceWaitIdle();
                }

                device.WaitIdle();
                Teardown();
                if (!Build())
                {
                    Thread.Sleep(50);
                    gap.Rebuilt(rebuilding.Elapsed.TotalSeconds);
                    return true;
                }

                stale = false;
                gap.Rebuilt(rebuilding.Elapsed.TotalSeconds);
                Console.WriteLine($"SCENE swapchain rebuilt at {extent.Width}x{extent.Height} in {rebuilding.Elapsed.TotalMilliseconds:F0} ms");
                return true; // the projection follows the new size
            }

            var carding = Stopwatch.StartNew();
            Guard("a card", () =>
            {
                if (film.Playing && !splashing && filmDrawn != (extent.Height, Words.Language))
                {
                    filmDrawn = (extent.Height, Words.Language);
                    filmTitle = Panel.CinemaTitle(extent.Height, film.Heading);
                    filmTitleTexture = Upload(filmTitle, filmTitleSet, filmTitleTexture);
                    filmHint = Panel.Hint(Words.Say("Any key: skip", "Une touche : passer"), extent.Height);
                    filmHintTexture = Upload(filmHint, filmHintSet, filmHintTexture);
                }

                if (cabinet.Playing && arcadeHintDrawn != (extent.Height, Words.Language))
                {
                    arcadeHintDrawn = (extent.Height, Words.Language);
                    arcadeHint = Panel.Hint(Words.Say("Esc, or a click away from the cabinet: leave the game", "Échap, ou un clic hors de la borne : quitter le jeu"), extent.Height);
                    arcadeHintTexture = Upload(arcadeHint, arcadeHintSet, arcadeHintTexture);
                }

                if (wheel.Shown > 0f && !splashing && wheelDrawn != (wheel.Pick, extent.Width, extent.Height, Words.Language))
                {
                    wheelDrawn = (wheel.Pick, extent.Width, extent.Height, Words.Language);
                    wheelCard = Panel.Wheel(wheel.Slots, wheel.Pick, wheel.Held, extent.Width, extent.Height);
                    wheelTexture = Upload(wheelCard, wheelSet, wheelTexture);
                }

                if (keysShown && !splashing && keysDrawn != (camera.Flying, extent.Width, extent.Height, Words.Language))
                {
                    keysDrawn = (camera.Flying, extent.Width, extent.Height, Words.Language);
                    keysCard = Panel.Keys(camera.Flying, extent.Width, extent.Height);
                    keysTexture = Upload(keysCard, keysSet, keysTexture);
                }

                if (rtxShown && !splashing && rtxDrawn != (raysOn, pathTracing, maxOut, (int)Math.Round(rtxFps), extent.Height, Words.Language))
                {
                    rtxDrawn = (raysOn, pathTracing, maxOut, (int)Math.Round(rtxFps), extent.Height, Words.Language);
                    rtxCard = Panel.RtxFeatures(gpu, driver, RtxRows(), rtxDrawn.Fps, extent.Height);
                    rtxTexture = Upload(rtxCard, rtxSet, rtxTexture);
                }

                if (shelf.Open && !splashing && (shelf.Version != cardVersion || extent.Height != cardHeight))
                {
                    cardVersion = shelf.Version;
                    cardHeight = extent.Height;
                    card = Panel.Draw(shelf.Levels, extent.Height);
                    cardTexture = Upload(card, cardSet, cardTexture);
                }

                if (googleChosen && aerial is not null && !splashing && aerialCardSize != (extent.Width, extent.Height))
                {
                    aerialCardSize = (extent.Width, extent.Height);
                    aerialCard = Panel.Attribution(aerialProviders, extent.Width, extent.Height);
                    aerialCardTexture = Upload(aerialCard, aerialCardSet, aerialCardTexture);
                }

                if (moment.HeardAge >= 0f && !splashing && (moment.HeardId != captionId || (extent.Width, extent.Height) != captionSize))
                {
                    captionId = moment.HeardId;
                    captionSize = (extent.Width, extent.Height);
                    caption = Panel.Caption(moment.Heard, moment.HeardTo, extent.Width, extent.Height);
                    captionTexture = Upload(caption, captionSet, captionTexture);
                }

                if (!splashing && phoneDrawn != (moment.Pairing is not null, moment.Devices > 0, extent.Height))
                {
                    phoneDrawn = (moment.Pairing is not null, moment.Devices > 0, extent.Height);
                    phone = Panel.Phone(extent.Height, phoneDrawn.Live, phoneDrawn.Connected);
                    phoneTexture = Upload(phone, phoneSet, phoneTexture);
                }

                if (moment.Guests > 0 && !splashing && guestDrawn != (moment.Guests, moment.GuestsHear, extent.Height, Words.Language))
                {
                    guestDrawn = (moment.Guests, moment.GuestsHear, extent.Height, Words.Language);
                    guestBadge = Panel.GuestBadge(moment.Guests, moment.GuestsHear, extent.Height);
                    guestTexture = Upload(guestBadge, guestSet, guestTexture);
                }

                if (!splashing && moment.Version != "" && versionDrawn != (moment.Version, extent.Height, Words.Language))
                {
                    versionDrawn = (moment.Version, extent.Height, Words.Language);
                    versionLabel = Panel.VersionLabel(moment.Version, extent.Height);
                    versionTexture = Upload(versionLabel, versionSet, versionTexture);
                }

                if (notesShown && !splashing && notesDrawn != (moment.Version, extent.Width, extent.Height, Words.Language))
                {
                    notesDrawn = (moment.Version, extent.Width, extent.Height, Words.Language);
                    notesCard = Panel.ReleaseNotes(moment.Version, extent.Width, extent.Height);
                    if (notesCard is not null)
                    {
                        notesTexture = Upload(notesCard, notesSet, notesTexture);
                    }
                }

                if (!splashing && meterTexture is null)
                {
                    meterTexture = Upload(Panel.Blank(), meterSet, null);
                }

                if (!splashing && micDrawn != (moment.MicOff, extent.Height))
                {
                    micDrawn = (moment.MicOff, extent.Height);
                    micButton = Panel.MicButton(moment.MicOff, extent.Height);
                    micTexture = Upload(micButton, micSet, micTexture);
                }

                if (!splashing && languageDrawn != (Words.Setting, extent.Height))
                {
                    languageDrawn = (Words.Setting, extent.Height);
                    languageButton = Panel.LanguageButton(Words.Setting, extent.Height);
                    languageTexture = Upload(languageButton, languageSet, languageTexture);
                }

                if (moment.Lease > 0f && !splashing && leaseDrawn != (extent.Height, Words.Language))
                {
                    leaseDrawn = (extent.Height, Words.Language);
                    leaseCard = Panel.Lease(extent.Height);
                    leaseTexture = Upload(leaseCard, leaseSet, leaseTexture);
                    leaseDot = Panel.Dot(extent.Height);
                    leaseDotTexture = Upload(leaseDot, leaseDotSet, leaseDotTexture);
                }

                var thinking = moment.State == "thinking";
                if (thinking && !thinkingWas)
                {
                    thinkingSince = now;
                }

                thinkingWas = thinking;
                if (thinking && !splashing && brainDrawn != extent.Height)
                {
                    brainDrawn = extent.Height;
                    brainCard = Panel.Brain(extent.Height);
                    brainTexture = Upload(brainCard, brainSet, brainTexture);
                }

                if (thinking && !splashing && leaseDrawn != (extent.Height, Words.Language)) // the ring's dot, for the brain's dots too
                {
                    leaseDrawn = (extent.Height, Words.Language);
                    leaseCard = Panel.Lease(extent.Height);
                    leaseTexture = Upload(leaseCard, leaseSet, leaseTexture);
                    leaseDot = Panel.Dot(extent.Height);
                    leaseDotTexture = Upload(leaseDot, leaseDotSet, leaseDotTexture);
                }

                if (pairingShown && !demo && clock.Elapsed.TotalSeconds - pairingOpened > 60.0)
                {
                    pairingShown = false;
                }

                var secondsLeft = (int)Math.Ceiling(moment.PairingLeft);
                if (pairingShown && !splashing && pairingDrawn != (moment.PairingId, secondsLeft, extent.Width, extent.Height, Words.Language))
                {
                    pairingDrawn = (moment.PairingId, secondsLeft, extent.Width, extent.Height, Words.Language);
                    pairingCard = Panel.Pairing(moment.Pairing, secondsLeft, moment.PairingId > 0 && moment.Pairing is null, extent.Width, extent.Height);
                    pairingTexture = Upload(pairingCard, pairingSet, pairingTexture);
                }

                if (!splashing && inviteDrawn != (extent.Height, Words.Language))
                {
                    inviteDrawn = (extent.Height, Words.Language);
                    inviteButton = Panel.InviteButton(extent.Height);
                    inviteTexture = Upload(inviteButton, inviteSet, inviteTexture);
                }

                if (moment.InvitationId != invitationSeen) // an invitation came, or why none did
                {
                    invitationSeen = moment.InvitationId;
                    invitationShown = true;
                    notesShown = false;
                    invitationOpened = clock.Elapsed.TotalSeconds;
                    pairingShown = false;
                }

                if (invitationShown && (clock.Elapsed.TotalSeconds - invitationOpened > 60.0 || (moment.Guests > 0 && moment.Invitation?.Rows is not null)))
                {
                    invitationShown = false;
                }

                if (invitationShown && !splashing && moment.Invitation is { } brought && invitationDrawn != (moment.InvitationId, extent.Width, extent.Height, Words.Language))
                {
                    invitationDrawn = (moment.InvitationId, extent.Width, extent.Height, Words.Language);
                    invitationCard = Panel.Invitation(brought, DateTime.Now.AddSeconds(moment.InvitationLeft), extent.Width, extent.Height);
                    invitationTexture = Upload(invitationCard, invitationSet, invitationTexture);
                }
            });
            if (carding.Elapsed.TotalMilliseconds >= 50)
            {
                Console.WriteLine($"SCENE cards drawn in {carding.Elapsed.TotalMilliseconds:F0} ms at {extent.Width}x{extent.Height}");
            }

            var slot = frame % FramesInFlight;
            device.WaitForFences([fences[slot]], true, -1);
            hallGallery.Frame(); // frees the gallery's meshes no frame still uses, and swaps in one redrawn from its file
            if (stamped[slot])
            {
                var stamps = stackalloc ulong[2];
                device.GetQueryPoolResults(frameStamps, 2 * slot, 2, new ReadOnlySpan<byte>(stamps, 16), new ByteSize64(8UL), VkQueryResultFlags._64Bit);
                if (stamps[1] > stamps[0])
                {
                    var gpuMs = (stamps[1] - stamps[0]) * tick / 1e6;
                    gpuTimes.Add(gpuMs);
                    Pace(cpuOf[slot] + gpuMs);
                }
            }

            Guard("Gaia module frame", () =>
            {
                gaiaModule?.PollAvailable(now);
                if (gaiaModule is not null) gaiaModule.Bridge = moment.Gaia;
                gaiaModule?.Advance(Words.Language);
            });
            Guard("agents' office frame", () =>
            {
                if (office is null || officeStage is null || officeFeed is null) return;
                if (office.Poll(now))
                {
                    officeStage.Reset();
                    officeFailed = false;
                }

                if (officeFailed) return;

                // Module code may throw: the first exception is one line in the log and turns the office off until a
                // new image replaces the code, rather than a stack trace every frame.
                try
                {
                    officeFeed.Advance(now);
                    var picked = officeSelection.Selected;
                    if (officeSelection.Validate(officeFeed.Sessions))
                    {
                        Console.WriteLine("WIDGETS agent-office selection let go: its session went or runs again");
                        wall.Released(picked);
                    }
                    office.Module.Update(new(Words.Language, quality == Quality.Flat, officeFeed.Sessions, officeSelection.Selected)
                    {
                        Turns = officeTurns,
                        Turning = officeDrag,
                    });
                    if (officeStage.Sync(office.Module)) Console.WriteLine($"WIDGETS agent-office tree: {officeStage.Instances} instances, {officeStage.Meshes} meshes ({officeStage.Changes}); native mesh buffers live {Mesh.LiveCount}");
                    else if (officeStage.Problems.Count > 0 && officeLogged != officeStage.Revision)
                    {
                        officeLogged = officeStage.Revision;
                        Console.Error.WriteLine($"WIDGETS agent-office tree refused, keeping the one shown: {string.Join("; ", officeStage.Problems.Take(5))}");
                    }

                    officeStage.Frame(office.Module, sceneClock); // the desks slow with the scene while the wheel is open
                }
                catch (Exception e)
                {
                    officeFailed = true;
                    officeStage.Items.Clear();
                    officeStage.Hits.Clear();
                    Console.Error.WriteLine($"WIDGETS agent-office off until its next image: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
                }
            });
            VkResult result;
            int imageIndex;
            try
            {
                if (frameGeneration)
                {
                    result = (VkResult)Streamline.AcquireNextImage(swapchain.VkHandle, acquired[slot].VkHandle, out var index);
                    imageIndex = (int)index;
                }
                else
                {
                    result = device.AcquireNextImageKHR(swapchain, -1, acquired[slot], default, out imageIndex);
                }
            }
            catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
            {
                result = e.Result; // BRUTAL throws Vulkan's errors; this one only says the window changed
                imageIndex = 0;
            }

            if (result == VkResult.ErrorOutOfDateKHR || (int)result < 0)
            {
                stale = true;
                return true;
            }

            videos?.Update(eye, hallGallery.Globes);
            *(Frame*)(frameMapped + (slot * frameStep)) = new Frame(eye, hazy ? (outside ? Haze * 0.12f : Haze) : 0f, materials.Photographed, rays is not null && raysOn && !pathTracing,
                videos?.Projected ?? GalleryVideo.Projection.None);
            device.ResetFences([fences[slot]]);
            device.ResetCommandPool(pools[slot], VkCommandPoolResetFlags.None);

            var cb = commands[slot];
            cb.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });
            cb.ResetQueryPool(frameStamps, 2 * slot, 2);
            cb.WriteTimestamp(VkPipelineStageFlags.TopOfPipeBit, frameStamps, 2 * slot);
            videos?.Record(cb, slot);
            rays?.Record(cb, slot);
            if (tracer is not null && pathTracing && !splashing)
            {
                tracer.Resize(extent.Width, extent.Height);
                tracer.Record(cb, slot, SN.Matrix4x4.CreateLookAt(eye, pose.Target, pose.Up), projection, eye, pose.Fov, hazy ? (outside ? Haze * 0.12f : Haze) : 0f, materials.Photographed,
                    hazy && quality == Quality.Low ? new SN.Vector4(0.075f, 0.09f, 0.115f, 0f) : new SN.Vector4(0.006f, 0.005f, 0.005f, 0f));
            }

            if (frameGeneration)
            {
                // Frames generated only between path-traced frames that Ray Reconstruction gave constants and tags,
                // and not in the few frames after the swapchain is made again.
                var generate = tracer is { Upscaling: true, UpscaleError: null } && pathTracing && !splashing && generationHeld <= 0;
                generationHeld--;
                if (Streamline.Generate(generate, images.Length, extent.Width, extent.Height, tracer?.Width ?? 0, tracer?.Height ?? 0) is { } refused)
                {
                    Console.WriteLine($"SCENE frame generation: {refused}");
                }

                if (Streamline.GenerationStatus != generationStatus)
                {
                    generationStatus = Streamline.GenerationStatus;
                    Console.WriteLine($"SCENE frame generation status: {Streamline.GenerationStatusName(generationStatus)}");
                }
            }
            if (arcadeGpu is not null) arcadeGpu.UploadAfterFence(cb, slot, arcade!.Frame);
            cabinet.WallParsec = wall.Showing == WallSource.Kind.Parsec;
            if (wall.Showing == WallSource.Kind.Pong && arcade is not null) pongWall?.UploadAfterFence(cb, slot, arcade.Frame);
            if (wall.Showing == WallSource.Kind.Session && wall.Session is { } shownSession)
            {
                RefreshWallCard(shownSession);
                sessionWall.UploadAfterFence(cb, slot, wallPixels, wallCardRevision);
            }
            cabinet.UploadAfterFence(cb, slot);
            Guard("lectern's reader", () => reader.Poll(now));
            var bookPage = reader.Picture;
            readerBook.UploadAfterFence(cb, slot, bookPage.Rgba, bookPage.Revision);
            clears[0] = new VkClearValue { Color = new VkClearColorValue { Float32 = outside && quality == Quality.Low ? DaySky : hazy && quality == Quality.Low ? HazeColor : new float4(0.006f, 0.005f, 0.005f, 1f) } }; // beyond the hall, all haze; beyond the streets, the sky
            clears[1] = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1f } };
            var splitCards = hudless && Streamline.Generating && !splashing; // the hall, then the cards, in two passes
            // DLSS Super Resolution (F7): the hall drawn smaller into the upscaler's pass, jittered, then upscaled.
            var upscaled = upscaler is not null && upscaling && !pathTracing && !splashing && !splitCards
                && upscaler.Resize(extent.Width, extent.Height) && upscaler.Ready;
            var drawn = upscaled ? new VkExtent2D { Width = upscaler!.Width, Height = upscaler.Height } : extent;
            var unjittered = viewProj;
            var jitter = default(SN.Vector2);
            if (upscaled)
            {
                (jitter, var clip) = upscaler!.Jitter(args.Contains("--dlss-jitter-flip") ? -1f : 1f);
                viewProj *= clip;
            }

            cb.BeginRenderPass(new VkRenderPassBeginInfo
            {
                RenderPass = upscaled ? upscaler!.Pass : splitCards ? hallPass : renderPass,
                Framebuffer = upscaled ? upscaler!.Framebuffer : framebuffers[imageIndex],
                RenderArea = new VkRect2D { Extent = drawn },
                ClearValueCount = 2,
                ClearValues = clears,
            }, VkSubpassContents.Inline);
            cb.SetViewport(firstViewport: 0, [new VkViewport { Width = drawn.Width, Height = drawn.Height, MinDepth = 0f, MaxDepth = 1f }]);
            cb.SetScissor(firstScissor: 0, [new VkRect2D { Extent = drawn }]);
            if (splashing)
            {
                cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                var opacity = Splash.Opacity(shown);
                for (var i = 0; i < splashSets.Length; i++)
                {
                    var push = new SplashPush(splashRects[i], new SN.Vector4(splashTints[i], opacity));
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [splashSets[i]], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }
            }
            else
            {
                cb.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
                cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 0, [frameSets[slot]], []);
                clocks.Draw((mesh, world, glow) => Draw(cb, layout, mesh, new Push(world, viewProj, glow)), DateTimeOffset.Now);
                cabinet.DrawMeshes((mesh, world, glow) => Draw(cb, layout, mesh, new Push(world, viewProj, glow)));
                if (gaiaModule is not null) Draw(cb, layout, gaiaModule.Surface, new Push(SN.Matrix4x4.Identity, viewProj, default));
                else if (gaiaFallback is not null) Draw(cb, layout, gaiaFallback[GaiaPresence.Status], new Push(SN.Matrix4x4.Identity, viewProj, default));
                for (var i = 0; i < parts.Length; i++)
                {
                    Draw(cb, layout, parts[i], new Push(worlds[i], viewProj, glows[i]));
                }

                if (playerShown)
                {
                    Draw(cb, layout, crate, new Push(SN.Matrix4x4.Identity, viewProj, default));
                    for (var i = 0; i < parts.Length; i++)
                    {
                        Draw(cb, layout, parts[i], new Push(playerWorlds[i], viewProj, playerGlows[i]));
                    }
                }
                if (officeStage is not null)
                {
                    foreach (var item in officeStage.Items)
                    {
                        Draw(cb, layout, item.Surface, new Push(item.World, viewProj, item.Glow));
                    }
                }

                // The knots' 1.2 million vertices of rope, and their cards, only when the picture shows the tables.
                if (KnotFloor.Seen(viewProj, knotBox))
                {
                    Draw(cb, layout, knotRopes, new Push(SN.Matrix4x4.Identity, viewProj, default));
                    Draw(cb, layout, knots[Words.Language == "fr"], new Push(SN.Matrix4x4.Identity, viewProj, default));
                }

                // The hall last: what stands in front of the floor and the wall is already in the depth buffer, so
                // their fragments hidden behind it are never shaded (nor their rays traced).
                Draw(cb, layout, hall, new Push(SN.Matrix4x4.Identity, viewProj, default));
                // The shell after it, out of the traced structure: the key light still reaches the hall through it.
                DrawLetThrough(cb, layout, shell, new Push(SN.Matrix4x4.Identity, viewProj, default));
                DrawLetThrough(cb, layout, shellGlass, new Push(SN.Matrix4x4.Identity, viewProj, new SN.Vector4(0.6f, 0.68f, 0.78f, 0.22f)));
                DrawLetThrough(cb, layout, shellBulbs, new Push(SN.Matrix4x4.Identity, viewProj, new SN.Vector4(1f, 0.85f, 0.6f, 1.4f)));
                DrawLetThrough(cb, layout, doorCurtain, new Push(streetDoor.World(), viewProj, default));
                // The streets, out of the traced structure: a kilometre of city would swell it for what the door shows.
                if (outside && googleChosen && aerial is not null)
                {
                    // Google's photographs, untraced: they carry their own light and shadows, and nothing else of the
                    // city is drawn beside them.
                    DrawUntraced(cb, layout, aerial, new Push(SN.Matrix4x4.Identity, viewProj, new SN.Vector4(0f, 0f, 0f, -1f)));
                }
                else if (streets is not null && outside)
                {
                    // Traced, so its buildings shade the streets; their shadows fall only outside (RayScene.CityMask).
                    var at = new Push(SN.Matrix4x4.Identity, viewProj, new SN.Vector4(0f, 0f, 0f, -1f)); // the shader's city()
                    Rays?.Remember(streets, at, outside: true);
                    DrawUntraced(cb, layout, streets, at);
                }
                // The gallery is traced too: under path tracing, what the structure lacks the picture lacks.
                hallGallery.Draw((mesh, glow) => Draw(cb, layout, mesh, new Push(SN.Matrix4x4.Identity, viewProj, glow)), viewProj, Words.Language == "fr");

                // Path traced, the picture goes over what the meshes drew, whose depth stays for the screens after.
                if (tracer is not null && pathTracing) tracer.Composite(cb, splashPipeline, splashLayout);

                // The Pong screen binds its own pipeline and descriptor set, so it comes after every mesh drawn with ours.
                chestView = worlds[Robot.Torso] * unjittered;
                arcadeGpu?.Draw(cb, slot, worlds[Robot.Torso] * viewProj);
                cabinet.DrawScreens(cb, slot, viewProj);
                if (wall.Showing == WallSource.Kind.Pong) pongWall?.Draw(cb, slot, ArcadeCabinet.WallFrame * viewProj);
                else if (wall.Showing == WallSource.Kind.Session) sessionWall.Draw(cb, slot, ArcadeCabinet.WallFrame * viewProj);
                if (hallGallery.Book is { } bookFrame) readerBook.Draw(cb, slot, bookFrame * viewProj);

                // The gallery's glass last, over what it holds and what stands behind it: through a copy of the hall drawn
                // so far (DLSS's pass, stopped and resumed), the path tracer's picture, or as a veil.
                if (hallGallery.Globes.Count > 0 && hallGallery.Shows(viewProj))
                {
                    var way = upscaled ? GlassWork.Way.Copy : tracer is not null && pathTracing ? GlassWork.Way.Traced : GlassWork.Way.Veil;
                    if (way != glassWay)
                    {
                        glassWay = way;
                        Console.WriteLine($"SCENE glass: {hallGallery.Globes.Count} globes, {way switch { GlassWork.Way.Copy => "bent through a copy of the hall", GlassWork.Way.Traced => "bent through the path tracer's picture", _ => "a veil (no picture to bend)" }}");
                    }

                    if (way == GlassWork.Way.Copy)
                    {
                        cb.EndRenderPass();
                        glass.Capture(cb, upscaler!.ColourImage, upscaler.Width, upscaler.Height);
                        cb.BeginRenderPass(new VkRenderPassBeginInfo
                        {
                            RenderPass = upscaler.ResumePass,
                            Framebuffer = upscaler.Framebuffer,
                            RenderArea = new VkRect2D { Extent = drawn },
                        }, VkSubpassContents.Inline);
                        cb.SetViewport(firstViewport: 0, [new VkViewport { Width = drawn.Width, Height = drawn.Height, MinDepth = 0f, MaxDepth = 1f }]);
                        cb.SetScissor(firstScissor: 0, [new VkRect2D { Extent = drawn }]);
                    }

                    glass.Draw(cb, way, hallGallery.Globes, viewProj, eye, (int)drawn.Width, (int)drawn.Height, way == GlassWork.Way.Traced ? tracer!.PictureView : default);
                }

                if (upscaled)
                {
                    // The hall drawn, DLSS upscales it; the window's image then takes it whole in the cards' pass, the
                    // cards and buttons drawn over it at the window's size.
                    cb.EndRenderPass();
                    upscaler!.Evaluate(cb, SN.Matrix4x4.CreateLookAt(eye, pose.Target, pose.Up), projection, eye, pose.Fov, jitter);
                    cb.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkDependencyFlags.None, [], [],
                        [Barrier(images[imageIndex], VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal, VkAccessFlags.None, VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit)]);
                    cb.BeginRenderPass(new VkRenderPassBeginInfo
                    {
                        RenderPass = cardsPass,
                        Framebuffer = framebuffers[imageIndex],
                        RenderArea = new VkRect2D { Extent = extent },
                    }, VkSubpassContents.Inline);
                    cb.SetViewport(firstViewport: 0, [new VkViewport { Width = extent.Width, Height = extent.Height, MinDepth = 0f, MaxDepth = 1f }]);
                    cb.SetScissor(firstScissor: 0, [new VkRect2D { Extent = extent }]);
                    upscaler.Composite(cb, splashPipeline, splashLayout);
                }

                if (splitCards)
                {
                    // The hall without its cards, copied by Streamline as frame generation's HUD-less colour, then the
                    // cards' pass over the same canvas.
                    cb.EndRenderPass();
                    var canvas = images[imageIndex];
                    cb.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [],
                        [Barrier(canvas, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferSrcOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit)]);
                    var untagged = Streamline.TagHudless(cb.VkHandle, canvas.VkHandle, canvasMemory[imageIndex].VkHandle, views[imageIndex].VkHandle, (int)extent.Width, (int)extent.Height,
                        (uint)surfaceFormat.Format, (uint)VkImageLayout.TransferSrcOptimal);
                    if (untagged is not null && !hudlessFailed)
                    {
                        hudlessFailed = true;
                        Console.WriteLine($"SCENE frame generation without its HUD-less colour: {untagged}");
                    }
                    else if (untagged is null && !hudlessTagged)
                    {
                        hudlessTagged = true;
                        Console.WriteLine("SCENE frame generation: the cards drawn apart, the hall tagged as HUD-less colour");
                    }

                    cb.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkDependencyFlags.None, [], [],
                        [Barrier(canvas, VkImageLayout.TransferSrcOptimal, VkImageLayout.ColorAttachmentOptimal, VkAccessFlags.TransferReadBit, VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit)]);
                    cb.BeginRenderPass(new VkRenderPassBeginInfo
                    {
                        RenderPass = cardsPass,
                        Framebuffer = framebuffers[imageIndex],
                        RenderArea = new VkRect2D { Extent = extent },
                    }, VkSubpassContents.Inline);
                    cb.SetViewport(firstViewport: 0, [new VkViewport { Width = extent.Width, Height = extent.Height, MinDepth = 0f, MaxDepth = 1f }]);
                    cb.SetScissor(firstScissor: 0, [new VkRect2D { Extent = extent }]);
                }

                if (shelf.Open && card is not null && cardTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var margin = CardMargin();
                    var push = new SplashPush(Rect(margin, margin, card.Width, card.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [cardSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (outside && googleChosen && aerial is not null && aerialCard is not null && aerialCardTexture is not null)
                {
                    // Google's attribution, bottom right, whenever its tiles are in view, the HUD shown or not.
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var push = new SplashPush(Rect(extent.Width - aerialCard.Width - CardMargin(), extent.Height - aerialCard.Height - CardMargin(), aerialCard.Width, aerialCard.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [aerialCardSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && moment.HeardAge >= 0f && caption is not null && captionTexture is not null && RoomCaptions.Shows(moment.HeardTo, RoomCaptions.Off))
                {
                    // Centred at the bottom; it comes in over 0.2 s and fades over the last 0.8 s.
                    var fade = Math.Clamp(Math.Min(moment.HeardAge / 0.2f, ((float)Link.HeardSeconds(moment.Heard) - moment.HeardAge) / 0.8f), 0f, 1f);
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var push = new SplashPush(Rect((extent.Width - caption.Width) / 2, extent.Height - caption.Height - CardMargin(), caption.Width, caption.Height), new SN.Vector4(1f, 1f, 1f, fade));
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [captionSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && moment.Lease > 0f && leaseCard is not null && leaseTexture is not null && leaseDot is not null && leaseDotTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (left, top) = (CardMargin(), extent.Height - leaseCard.Height - CardMargin());
                    var push = new SplashPush(Rect(left, top, leaseCard.Width, leaseCard.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [leaseSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                    var lit = (int)MathF.Ceiling(Panel.LeaseDots * Math.Clamp(moment.LeaseLength > 0f ? moment.Lease / moment.LeaseLength : 0f, 0f, 1f));
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [leaseDotSet], []);
                    for (var i = 0; i < Panel.LeaseDots; i++)
                    {
                        var (x, y) = Panel.LeaseDot(i, extent.Height);
                        var tint = i >= Panel.LeaseDots - lit ? new SN.Vector4(0.353f, 0.863f, 0.51f, 1f) : new SN.Vector4(0.235f, 0.259f, 0.282f, 1f);
                        var dot = new SplashPush(Rect(left + (int)MathF.Round(x - (leaseDot.Width / 2f)), top + (int)MathF.Round(y - (leaseDot.Height / 2f)), leaseDot.Width, leaseDot.Height), tint);
                        cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&dot, sizeof(SplashPush)));
                        cb.Draw(6, 1, 0, 0);
                    }
                }

                if (hud && moment.State == "thinking" && brainCard is not null && brainTexture is not null)
                {
                    // Bottom left, or right of the ring's card when Delta listens without its name, centred on it.
                    var ring = moment.Lease > 0f && leaseCard is not null ? leaseCard : null;
                    var left = CardMargin() + (ring is null ? 0 : ring.Width + (CardMargin() / 2));
                    var top = extent.Height - CardMargin() - (ring is null ? brainCard.Height : (ring.Height + brainCard.Height) / 2);
                    var push = new SplashPush(Rect(left, top, brainCard.Width, brainCard.Height), new SN.Vector4(1f, 1f, 1f, Panel.BrainBeat(now - thinkingSince)));
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [brainSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                    var dots = Panel.ElapsedDots(now - thinkingSince);
                    if (dots > 0 && leaseDot is not null && leaseDotTexture is not null)
                    {
                        // A dot per ten seconds of work, in a row right of the brain, amber like it.
                        cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [leaseDotSet], []);
                        for (var i = 0; i < dots; i++)
                        {
                            var dot = new SplashPush(Rect(left + brainCard.Width + (CardMargin() / 3) + (i * leaseDot.Width * 2), top + ((brainCard.Height - leaseDot.Height) / 2), leaseDot.Width, leaseDot.Height), new SN.Vector4(1f, 0.745f, 0.353f, 1f));
                            cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&dot, sizeof(SplashPush)));
                            cb.Draw(6, 1, 0, 0);
                        }
                    }
                }

                if (hud && moment.Guests > 0 && guestBadge is not null && guestTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (guestX, guestY) = GuestAt();
                    var push = new SplashPush(Rect(guestX, guestY, guestBadge.Width, guestBadge.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [guestSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && micButton is not null && micTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (micX, micY) = MicAt();
                    var push = new SplashPush(Rect(micX, micY, micButton.Width, micButton.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [micSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && versionLabel is not null && versionTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (versionX, versionY) = VersionAt();
                    var push = new SplashPush(Rect(versionX, versionY, versionLabel.Width, versionLabel.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [versionSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && micButton is not null && meterTexture is not null && !moment.MicOff && moment.MicLevel > 0f)
                {
                    var loud = Math.Clamp(((20f * MathF.Log10(Math.Max(moment.MicLevel, 1e-5f))) + 60f) / 50f, 0f, 1f);
                    var (micX, micY) = MicAt();
                    var edge = Math.Max(2, extent.Height / 180);
                    var length = (int)((micButton.Width - (2 * edge)) * loud);
                    if (length > 0)
                    {
                        cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                        var push = new SplashPush(Rect(micX + edge, micY + micButton.Height - (2 * edge), length, edge), new SN.Vector4(0.353f, 0.863f, 0.51f, 1f));
                        cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [meterSet], []);
                        cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                        cb.Draw(6, 1, 0, 0);
                    }
                }

                if (hud && languageButton is not null && languageTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (languageX, languageY) = LanguageAt();
                    var push = new SplashPush(Rect(languageX, languageY, languageButton.Width, languageButton.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [languageSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && phone is not null && phoneTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (phoneX, phoneY) = PhoneAt();
                    var push = new SplashPush(Rect(phoneX, phoneY, phone.Width, phone.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [phoneSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && inviteButton is not null && inviteTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (inviteX, inviteY) = InviteAt();
                    var push = new SplashPush(Rect(inviteX, inviteY, inviteButton.Width, inviteButton.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [inviteSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && pairingShown && pairingCard is not null && pairingTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (cardX, cardY) = PairingAt();
                    var push = new SplashPush(Rect(cardX, cardY, pairingCard.Width, pairingCard.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [pairingSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && invitationShown && invitationCard is not null && invitationTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (cardX, cardY) = InvitationAt();
                    var push = new SplashPush(Rect(cardX, cardY, invitationCard.Width, invitationCard.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [invitationSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (hud && notesShown && notesCard is not null && notesTexture is not null)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var (notesX, notesY) = NotesAt();
                    var push = new SplashPush(Rect(notesX, notesY, notesCard.Width, notesCard.Height), SN.Vector4.One);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [notesSet], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                // The camera's own layers, over the rest: a dot where a locked view aims, the keys' card, the wheel
                // over a dimmed scene, and last the cinematic's bars, black, title and hint.
                void Quad(VkDescriptorSet set, int left, int top, int width, int height, SN.Vector4 tint)
                {
                    cb.BindPipeline(VkPipelineBindPoint.Graphics, splashPipeline);
                    var push = new SplashPush(Rect(left, top, width, height), tint);
                    cb.BindDescriptorSets(VkPipelineBindPoint.Graphics, splashLayout, 0, [set], []);
                    cb.PushConstants(splashLayout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(SplashPush)));
                    cb.Draw(6, 1, 0, 0);
                }

                if (locked && !wheel.Open && !film.Playing && meterTexture is not null)
                {
                    var dot = Math.Max(3, extent.Height / 240);
                    Quad(meterSet, (extent.Width - dot) / 2, (extent.Height - dot) / 2, dot, dot, new SN.Vector4(1f, 1f, 1f, 0.8f));
                }

                if (hud && cabinet.Playing && !film.Playing && arcadeHint is not null && arcadeHintTexture is not null && meterTexture is not null)
                {
                    var margin = CardMargin();
                    var (left, top) = ((extent.Width - arcadeHint.Width) / 2, extent.Height - arcadeHint.Height - margin);
                    Quad(meterSet, left - margin / 2, top - margin / 4, arcadeHint.Width + margin, arcadeHint.Height + margin / 2, new SN.Vector4(0f, 0f, 0f, 0.55f));
                    Quad(arcadeHintSet, left, top, arcadeHint.Width, arcadeHint.Height, SN.Vector4.One);
                }

                if (keysShown && !film.Playing && wheel.Shown <= 0f && keysCard is not null && keysTexture is not null)
                {
                    Quad(keysSet, CardMargin(), extent.Height - keysCard.Height - CardMargin(), keysCard.Width, keysCard.Height, SN.Vector4.One);
                }

                if (rtxShown && !film.Playing && wheel.Shown <= 0f && rtxCard is not null && rtxTexture is not null)
                {
                    Quad(rtxSet, extent.Width - rtxCard.Width - CardMargin(), (extent.Height - rtxCard.Height) / 2, rtxCard.Width, rtxCard.Height, SN.Vector4.One);
                }

                if (wheel.Shown > 0f && wheelCard is not null && wheelTexture is not null && meterTexture is not null)
                {
                    var k = wheel.Shown;
                    Quad(meterSet, 0, 0, extent.Width, extent.Height, new SN.Vector4(0f, 0f, 0.02f, 0.55f * k));
                    var grow = 0.85f + (0.15f * k);
                    var (wide, high) = ((int)MathF.Round(wheelCard.Width * grow), (int)MathF.Round(wheelCard.Height * grow));
                    Quad(wheelSet, (extent.Width - wide) / 2, (extent.Height - high) / 2, wide, high, new SN.Vector4(1f, 1f, 1f, k));
                }

                if (film.Playing && meterTexture is not null)
                {
                    var bar = (int)MathF.Round(film.BarHeight(aspect) * extent.Height);
                    if (film.Black > 0f)
                    {
                        Quad(meterSet, 0, 0, extent.Width, extent.Height, new SN.Vector4(0f, 0f, 0f, film.Black));
                    }

                    if (bar > 0)
                    {
                        Quad(meterSet, 0, 0, extent.Width, bar, new SN.Vector4(0f, 0f, 0f, 1f));
                        Quad(meterSet, 0, extent.Height - bar, extent.Width, bar, new SN.Vector4(0f, 0f, 0f, 1f));
                    }

                    if (film.Title > 0f && filmTitle is not null && filmTitleTexture is not null)
                    {
                        Quad(filmTitleSet, (extent.Width - filmTitle.Width) / 2, (extent.Height - filmTitle.Height) / 2, filmTitle.Width, filmTitle.Height, new SN.Vector4(1f, 1f, 1f, film.Title));
                    }

                    if (hud && film.Hint > 0f && filmHint is not null && filmHintTexture is not null)
                    {
                        var margin = CardMargin();
                        var hintTop = bar >= filmHint.Height ? extent.Height - bar + ((bar - filmHint.Height) / 2) : extent.Height - filmHint.Height - margin; // in the bottom bar
                        Quad(filmHintSet, extent.Width - filmHint.Width - margin, hintTop, filmHint.Width, filmHint.Height,
                            new SN.Vector4(1f, 1f, 1f, film.Hint));
                    }
                }
            }

            cb.EndRenderPass();
            var capturing = shot < shots.Length && shown >= shots[shot].At && extent.Width == shotExtent.Width && extent.Height == shotExtent.Height;
            var filming = (arcadeFilm is { Rolling: true } || origamiFilm is { Rolling: true }) && extent.Width == shotExtent.Width && extent.Height == shotExtent.Height;
            if (capturing || filming)
            {
                cb.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [],
                    [Barrier(images[imageIndex], VkImageLayout.PresentSrcKHR, VkImageLayout.TransferSrcOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit)]);
                cb.CopyImageToBuffer(images[imageIndex], VkImageLayout.TransferSrcOptimal, readback,
                    [new VkBufferImageCopy { ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 }, ImageExtent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 } }]);
                cb.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.BottomOfPipeBit, VkDependencyFlags.None, [], [],
                    [Barrier(images[imageIndex], VkImageLayout.TransferSrcOptimal, VkImageLayout.PresentSrcKHR, default, default)]);
            }

            if (frameGeneration)
            {
                // The canvas's sRGB bytes into Streamline's UNORM back buffer as they are, which it presents.
                var canvas = images[imageIndex];
                var back = streamlineImages[imageIndex];
                cb.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [],
                [
                    Barrier(canvas, VkImageLayout.PresentSrcKHR, VkImageLayout.TransferSrcOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit),
                    Barrier(back, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VkAccessFlags.None, VkAccessFlags.TransferWriteBit),
                ]);
                var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
                cb.CopyImage(canvas, VkImageLayout.TransferSrcOptimal, back, VkImageLayout.TransferDstOptimal,
                    [new VkImageCopy { SrcSubresource = layers, DstSubresource = layers, Extent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 } }]);
                cb.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.BottomOfPipeBit, VkDependencyFlags.None, [], [],
                    [Barrier(back, VkImageLayout.TransferDstOptimal, VkImageLayout.PresentSrcKHR, VkAccessFlags.TransferWriteBit, VkAccessFlags.None)]);
            }

            cb.WriteTimestamp(VkPipelineStageFlags.BottomOfPipeBit, frameStamps, (2 * slot) + 1);
            stamped[slot] = true;
            cb.End();

            var waitStage = VkPipelineStageFlags.ColorAttachmentOutputBit;
            var commandHandle = cb.Handle;
            var waitSemaphore = acquired[slot];
            var signalSemaphore = renderDone[imageIndex];
            var frameId = presentCount + 1;
            var submitted = new VkLatencySubmissionPresentIdNV { PresentID = (nint)frameId };
            if (reflex is not null)
            {
                reflex.Mark(swapchain, frameId, VkLatencyMarkerNV.SimulationEndNV);
                reflex.Mark(swapchain, frameId, VkLatencyMarkerNV.RendersubmitStartNV);
            }
            else if (frameGeneration)
            {
                Streamline.Mark(Streamline.SimulationEnd);
                Streamline.Mark(Streamline.RenderSubmitStart);
            }

            queue.Submit(
                [
                    new VkSubmitInfo
                    {
                        Next = reflex is null ? null : &submitted,
                        WaitSemaphoreCount = 1,
                        WaitSemaphores = &waitSemaphore,
                        WaitDstStageMask = &waitStage,
                        CommandBufferCount = 1,
                        CommandBuffers = &commandHandle,
                        SignalSemaphoreCount = 1,
                        SignalSemaphores = &signalSemaphore,
                    },
                ],
                fences[slot]);
            cpuOf[slot] = (clock.Elapsed.TotalSeconds - inputAt) * 1000.0;
            var presentIndex = imageIndex;
            var presentId = (nint)(++presentCount);
            var presentIdInfo = new VkPresentIdKHR { SwapchainCount = 1, PresentIds = &presentId };
            var presentSwapchain = swapchain; // Build reassigns swapchain, whose address cannot be taken
            if (reflex is not null)
            {
                reflex.Mark(swapchain, frameId, VkLatencyMarkerNV.RendersubmitEndNV);
                reflex.Mark(swapchain, frameId, VkLatencyMarkerNV.PresentStartNV);
            }
            else if (frameGeneration)
            {
                Streamline.Mark(Streamline.RenderSubmitEnd);
                Streamline.Mark(Streamline.PresentStart);
            }

            var presentInfo = new VkPresentInfoKHR
            {
                WaitSemaphoreCount = 1,
                WaitSemaphores = &signalSemaphore,
                SwapchainCount = 1,
                Swapchains = &presentSwapchain,
                ImageIndices = &presentIndex,
                Next = paceByScreen || reflex is not null ? &presentIdInfo : null,
            };
            try
            {
                // With Streamline (--dlss), through its interposer, which keeps its books there once a frame.
                result = Streamline.Presents ? (VkResult)Streamline.Present(queue.VkHandle, &presentInfo) : queue.PresentKHR(presentInfo);
            }
            catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
            {
                result = e.Result; // the window changed while the frame was drawn, as on 2026-10-22 at 16:35
            }

            if (reflex is not null)
            {
                reflex.Mark(presentSwapchain, frameId, VkLatencyMarkerNV.PresentEndNV);
                reflex.Read(presentSwapchain);
            }
            else if (frameGeneration)
            {
                Streamline.Mark(Streamline.PresentEnd);
            }

            if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            {
                stale = true;
                lastPresented = 0; // the next swapchain's frames are waited for, not this one's
            }
            else if (paceByScreen && result == VkResult.Success)
            {
                // One frame ahead, the next frame waits for the one before this: it is drawn while this one waits.
                var older = ahead && lastPresented != 0;
                waitFor = older ? lastPresented : presentId;
                waitForInput = older ? lastInput : inputAt;
                lastPresented = presentId;
                lastInput = inputAt;
                aheadFrames += older ? 1 : 0;
            }

            var presented = filmDir is null ? clock.Elapsed.TotalSeconds : now;
            if (result == VkResult.Success && !splashing && gaiaModule is not null && gaiaModule.Version != modulePresentedVersion)
            {
                modulePresentedVersion = gaiaModule.Version;
                moduleTestSawSwap = true;
                if (gaiaModule.LastRequestTimestamp > 0)
                    Console.WriteLine($"MODULE gaia v{gaiaModule.Version.ToString(2)} first presentation submitted after click {Stopwatch.GetElapsedTime(gaiaModule.LastRequestTimestamp).TotalMilliseconds:F2} ms (monotonic, not display scanout); native mesh buffers live {Mesh.LiveCount}; pid {Environment.ProcessId}");
            }
            if (moduleClickTest && !moduleTestGcChecked && linkNow >= 3.5 && gaiaModule?.LastRetiredCode is { } retiredCode)
            {
                moduleTestGcChecked = true;
                for (var attempt = 0; attempt < 3 && retiredCode.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
                moduleTestCollected = !retiredCode.IsAlive;
                Console.WriteLine($"MODULE demo-test retired code context collected {moduleTestCollected}; native mesh buffers live {Mesh.LiveCount}; pid {Environment.ProcessId}");
            }
            if (firstFrame < 0)
            {
                firstFrame = presented;
                nextFrame = presented;
                Console.WriteLine($"SCENE first frame presented after {presented * 1000:F0} ms");
            }
            else if (gap.Paused)
            {
                // A minimised or hidden spell is not a frame the window took to draw.
                if (presented - lastPresent >= 0.25)
                {
                    Console.WriteLine($"SCENE pause {(presented - lastPresent) * 1000.0:F0} ms, not counted as a frame, at {presented - firstFrame:F2} s: {gap.Describe(presented - lastPresent)}");
                }
            }
            else
            {
                frameTimes.Add((presented - lastPresent) * 1000.0); // present to present
                var each = presented - lastPresent;
                rtxFps = rtxFps <= 0 ? 1.0 / each : rtxFps + ((1.0 / each) - rtxFps) * Math.Min(1.0, each); // F10's fps
                if (presented - lastPresent >= 0.25)
                {
                    // Where in the log a long frame falls says what came before it; where its time went, what the window did.
                    Console.WriteLine($"SCENE slow frame {(presented - lastPresent) * 1000.0:F0} ms, frame {frame} at {presented - firstFrame:F2} s: {gap.Describe(presented - lastPresent)}");
                }
            }

            lastPresent = presented;
            gap.Begin(presented);
            frame++;
            if (capturing)
            {
                device.WaitIdle();
                var path = Path.Combine(shotsDir!, shots[shot].Name + ".png");
                var bgra = surfaceFormat.Format.ToString().StartsWith("B8G8R8", StringComparison.Ordinal);
                Png.Save(path, new ReadOnlySpan<byte>((byte*)readbackMapped, (int)(extent.Width * extent.Height * 4)), extent.Width, extent.Height, bgra);
                Console.WriteLine($"SCENE shot {path} at {now:F2} s ({moment.State}, level {moment.Level:F2}, context {moment.Context:F2}{(moment.Compacting ? ", compacting" : "")})");
                shot++;
                lastPresent = clock.Elapsed.TotalSeconds; // the capture's wait is not a frame time
                gap.Begin(lastPresent);
            }

            if (filming)
            {
                device.WaitIdle();
                var bgra = surfaceFormat.Format.ToString().StartsWith("B8G8R8", StringComparison.Ordinal);
                encoder ??= Process.Start(new ProcessStartInfo("ffmpeg")
                {
                    ArgumentList =
                    {
                        "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", bgra ? "bgra" : "rgba", "-s", $"{extent.Width}x{extent.Height}",
                        "-r", $"{FilmFps}", "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "17", "-pix_fmt", "yuv420p",
                        "-movflags", "+faststart", Path.Combine(filmDir!, origamiFilmed ? "origami-film.mp4" : "parsec-film-silent.mp4"),
                    },
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                }) ?? throw new InvalidOperationException("FFmpeg did not start");
                encoder.StandardInput.BaseStream.Write(new ReadOnlySpan<byte>((byte*)readbackMapped, (int)(extent.Width * extent.Height * 4)));
                filmFrames++;
                if (arcadeFilm is not null)
                {
                    filmSound.AddRange(cabinet.TakeSound());
                }
            }
            else if (arcadeFilm is not null)
            {
                cabinet.TakeSound(); // the game's sound before the film rolls is not the film's
            }

            if (cap > 0 && reflex is not { LowLatency: true })
            {
                // Pace to the cap: sleep while more than 1.5 ms is left, then spin. A frame that falls more than one
                // period behind resets the schedule instead of bursting to catch up.
                nextFrame += period;
                if (clock.Elapsed.TotalSeconds - nextFrame > period)
                {
                    nextFrame = clock.Elapsed.TotalSeconds;
                }

                while (nextFrame - clock.Elapsed.TotalSeconds > 0.0015)
                {
                    Thread.Sleep(1);
                }

                while (clock.Elapsed.TotalSeconds < nextFrame)
                {
                    Thread.SpinWait(20);
                }
            }

            return true;
        }

        var loopSeconds = (clock.Elapsed - loopStart).TotalSeconds;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuStart;
        if (cap > 0 && OperatingSystem.IsWindows())
        {
            _ = TimeEndPeriod(1);
        }

        if (shot < shots.Length)
        {
            Console.WriteLine($"SCENE missing shots: the run ended at {loopSeconds:F1} s, before {shots[shot].Name} at {shots[shot].At} s");
        }

        if (encoder is not null && origamiFilmed)
        {
            encoder.StandardInput.Close();
            encoder.WaitForExit();
            Console.WriteLine($"SCENE film {filmFrames} frames ({filmFrames / (double)FilmFps:F1} s), silent, FFmpeg exit {encoder.ExitCode}, {Path.Combine(filmDir!, "origami-film.mp4")}");
            encoder.Dispose();
        }
        else if (encoder is not null)
        {
            encoder.StandardInput.Close();
            encoder.WaitForExit();
            encoder.Dispose();
            var silent = Path.Combine(filmDir!, "parsec-film-silent.mp4");
            var sound = Path.Combine(filmDir!, "parsec-film.wav");
            var movie = Path.Combine(filmDir!, "parsec-film.mp4");
            ArcadeFilm.SaveWav(sound, CollectionsMarshal.AsSpan(filmSound));
            using (var mux = Process.Start(new ProcessStartInfo("ffmpeg")
            {
                ArgumentList = { "-loglevel", "error", "-y", "-i", silent, "-i", sound, "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", movie },
                UseShellExecute = false,
            }))
            {
                mux?.WaitForExit();
                Console.WriteLine($"SCENE film {filmFrames} frames ({filmFrames / (double)FilmFps:F1} s), sound {filmSound.Count / (double)ScriptAudio.SampleRate:F1} s, FFmpeg exit {mux?.ExitCode}, {movie}");
            }

            File.Delete(silent);
            File.Delete(sound);
        }

        // Clean shutdown, in reverse order of creation.
        if (frameGeneration)
        {
            Streamline.Generate(false, images.Length, extent.Width, extent.Height, 0, 0);
            Streamline.DeviceWaitIdle();
            Console.WriteLine($"SCENE frame generation: {Streamline.FramesPerDrawnAverage:F2} frames presented for each drawn while on (last {Streamline.FramesPerDrawn}); status {Streamline.GenerationStatusName(Streamline.GenerationStatus)}, up to {Streamline.FramesToGenerateMax} generated between two");
        }

        device.WaitIdle();
        for (var i = 0; i < FramesInFlight; i++)
        {
            device.DestroySemaphore(acquired[i], null);
            device.DestroyFence(fences[i], null);
            device.DestroyCommandPool(pools[i], null);
        }

        device.DestroyQueryPool(frameStamps, null);

        if (readsBack)
        {
            device.UnmapMemory(readbackMemory);
            device.DestroyBuffer(readback, null);
            device.FreeMemory(readbackMemory, null);
        }

        foreach (var part in parts)
        {
            part.Dispose();
        }

        crate.Dispose();

        gaiaModule?.Dispose();
        officeFeed?.Dispose();
        if (officeStage is not null)
        {
            Console.WriteLine($"WIDGETS agent-office shutdown: meshes built {officeStage.Built}, released before shutdown {officeStage.Released}, {officeStage.Meshes} still held; code reloads {office?.Reloads ?? 0}");
        }

        officeStage?.Dispose();
        office?.Dispose();

        foreach (var presence in gaiaFallback?.Values ?? Enumerable.Empty<Mesh>())
        {
            presence.Dispose();
        }
        hall.Dispose();
        knotRopes.Dispose();
        shell.Dispose();
        shellGlass.Dispose();
        doorCurtain.Dispose();
        streets?.Dispose();
        aerial?.Dispose();
        aerialImage.Dispose();
        aerialCardTexture?.Dispose();
        shellBulbs.Dispose();
        hallGallery.Dispose();
        foreach (var gallery in knots.Values)
        {
            gallery.Dispose();
        }

        clocks.Dispose();
        Console.WriteLine($"SCENE native mesh buffers remaining after disposal {Mesh.LiveCount}");
        foreach (var texture in splashTextures)
        {
            texture.Dispose();
        }

        cardTexture?.Dispose();
        captionTexture?.Dispose();
        phoneTexture?.Dispose();
        languageTexture?.Dispose();
        micTexture?.Dispose();
        guestTexture?.Dispose();
        pairingTexture?.Dispose();
        inviteTexture?.Dispose();
        invitationTexture?.Dispose();
        meterTexture?.Dispose();
        versionTexture?.Dispose();
        notesTexture?.Dispose();
        leaseTexture?.Dispose();
        brainTexture?.Dispose();
        leaseDotTexture?.Dispose();
        filmTitleTexture?.Dispose();
        filmHintTexture?.Dispose();
        arcadeHintTexture?.Dispose();
        wheelTexture?.Dispose();
        keysTexture?.Dispose();
        arcadeGpu?.Dispose();
        pongWall?.Dispose();
        sessionWall.Dispose();
        readerBook.Dispose();
        reader.Dispose();
        cabinet.Dispose();
        arcade?.Dispose();
        Console.WriteLine($"ARCADE shutdown: uploads {arcadeGpu?.Uploads ?? 0}; native texture images remaining {Texture.LiveCount}; pid {Environment.ProcessId}");
        device.DestroyCommandPool(uploadPool, null);

        device.DestroyDescriptorPool(descriptorPool, null);
        device.DestroyPipeline(splashPipeline, null);
        device.DestroyPipelineLayout(splashLayout, null);
        device.DestroyDescriptorSetLayout(setLayout, null);
        device.DestroySampler(sampler, null);
        device.DestroySampler(materialSampler, null);
        materials.Dispose();
        device.DestroySampler(globeSampler, null);
        globes.Dispose();
        videos?.Dispose();
        pictures.Dispose();
        if (tracer is { GpuMilliseconds.Count: > 0 })
        {
            Console.WriteLine($"SCENE path tracing {tracer.Measured()} at {tracer.Width}x{tracer.Height}");
        }

        if (upscaler is not null)
        {
            Console.WriteLine($"SCENE super resolution: {upscaler.Measured()}");
        }

        if (reflex is not null)
        {
            Console.WriteLine($"SCENE reflex {reflex.Measured()}");
        }

        reflex?.Dispose();
        tracer?.Dispose();
        upscaler?.Dispose();
        glass.Dispose();
        rays?.Dispose();
        Rays = null;
        device.DestroyShaderModule(splashVertex, null);
        device.DestroyShaderModule(splashFragment, null);
        device.DestroyPipeline(pipeline, null);
        device.DestroyPipelineLayout(layout, null);
        device.DestroyDescriptorPool(framePool, null);
        device.DestroyDescriptorSetLayout(frameLayout, null);
        device.UnmapMemory(frameMemory);
        device.DestroyBuffer(frameBuffer, null);
        device.FreeMemory(frameMemory, null);
        device.DestroyShaderModule(vertexModule, null);
        device.DestroyShaderModule(fragmentModule, null);
        Teardown();
        device.DestroyRenderPass(renderPass, null);
        if (hudless || superResolution)
        {
            device.DestroyRenderPass(hallPass, null);
            device.DestroyRenderPass(cardsPass, null);
        }

        if (frameGeneration)
        {
            Streamline.DestroySwapchain(swapchain.VkHandle);
        }
        else
        {
            device.DestroySwapchainKHR(swapchain, null);
        }

        tray?.Dispose();
        Streamline.Shutdown();
        device.Dispose();
        instance.DestroySurfaceKHR(surface, null);
        instance.Dispose();
        window.Dispose();
        Glfw.Terminate();

        double P(double q) => frameTimes.Percentile(q);
        Console.WriteLine($"SCENE frames {frame} in {loopSeconds:F2} s ({frame / loopSeconds:F0} fps); frame ms p50 {P(0.5):F3} p95 {P(0.95):F3} p99 {P(0.99):F3} max {frameTimes.Max:F3}");
        if (gpuTimes.Count > 0)
        {
            Console.WriteLine($"SCENE gpu ms a frame p50 {gpuTimes.Percentile(0.5):F2} p95 {gpuTimes.Percentile(0.95):F2} max {gpuTimes.Max:F2}{(paceByScreen ? $"; one frame ahead for {aheadFrames} of {frame} frames" : "")}");
        }
        if (screenTimes.Count > 0)
        {
            double Q(double q) => screenTimes.Percentile(q);
            Console.WriteLine($"SCENE keys to screen ms p50 {Q(0.5):F3} p95 {Q(0.95):F3} p99 {Q(0.99):F3} max {screenTimes.Max:F3} over {screenTimes.Count} frames");
        }

        Console.WriteLine($"SCENE CPU {cpu.TotalSeconds:F2} s over {loopSeconds:F2} s ({100 * cpu.TotalSeconds / loopSeconds / Environment.ProcessorCount:F1} % of {Environment.ProcessorCount} logical CPUs), peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB");
        Console.WriteLine($"SCENE closed by {reason} after {clock.Elapsed.TotalSeconds:F2} s");
        if (reason is "window closed")
        {
            Closed(port, true);
        }

        if (wallTest) Console.WriteLine(wallTestStep == 3 && wallTestFailures == 0 ? "WALLTEST PASSED" : $"WALLTEST FAILED {wallTestFailures} of {wallTestStep} checks made, 3 expected");
        if (wallReal is not null) Console.WriteLine(wallTestStep == 2 && wallTestFailures == 0 ? "WALLREAL PASSED" : $"WALLREAL FAILED {wallTestFailures}, step {wallTestStep} of 2");
        if (walkTest) Console.WriteLine(walkChecks == 10 && walkFailures == 0 ? "WALKTEST PASSED" : $"WALKTEST FAILED {walkFailures} of {walkChecks} checks made, 10 expected");
        return (moduleClickTest && (!moduleTestClicked || !moduleTestSawSwap || !moduleTestCollected || Mesh.LiveCount != 0)) || (wallTest && (wallTestStep != 3 || wallTestFailures > 0))
            || (walkTest && (walkChecks != 10 || walkFailures > 0))
            || (wallReal is not null && (wallTestStep != 2 || wallTestFailures > 0)) ? 1 : 0;
    }

    // %LOCALAPPDATA%\delta\scene-<port>.closed says that the user closed the window listening on that port, so that a
    // brain, which may not have started it, leaves it closed until something asks for it (brain/scene.go).
    private static void Closed(int port, bool closed)
    {
        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "delta", $"scene-{port}.closed");
        try
        {
            if (closed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                File.Delete(marker);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"SCENE {marker}: {e.Message}");
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    // The hall for ray queries, when they are traced: each mesh drawn is remembered for the next frame's structure.
    private static RayScene? Rays;

    private static void Draw(CommandBuffer cb, VkPipelineLayout layout, Mesh mesh, Push push)
    {
        Rays?.Remember(mesh, push);
        DrawUntraced(cb, layout, mesh, push);
    }

    // Drawn and traced, but passed by shadow rays: the hall's shell (HallShell) is seen by the path tracer and by the
    // occlusion's rays, while the key light still comes in through it.
    private static void DrawLetThrough(CommandBuffer cb, VkPipelineLayout layout, Mesh mesh, Push push)
    {
        Rays?.Remember(mesh, push, shadows: false);
        DrawUntraced(cb, layout, mesh, push);
    }

    // Drawn, but left out of the next frame's traced structure: it casts no traced shadow nor occlusion.
    private static void DrawUntraced(CommandBuffer cb, VkPipelineLayout layout, Mesh mesh, Push push)
    {
        cb.PushConstants(layout, PushStages, offset: 0, new ReadOnlySpan<byte>(&push, sizeof(Push)));
        cb.BindVertexBuffer(0, mesh.Buffer);
        cb.Draw(mesh.Count, 1, 0, 0);
    }

    // The value after the option name, if both are there.
    // The device extensions the GPU offers.
    private static HashSet<string> DeviceExtensions(PhysicalDevice physical)
    {
        var count = physical.EnumerateDeviceExtensionProperties_Count(default);
        var properties = new VkExtensionProperties[count];
        physical.EnumerateDeviceExtensionProperties_Write(default, properties);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var copy = property;
            names.Add(Marshal.PtrToStringUTF8((nint)copy.ExtensionName) ?? "");
        }

        return names;
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    // A colour image's layout change, as HallSlice writes it.
    internal static VkImageMemoryBarrier Barrier(VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlags src, VkAccessFlags dst) => new()
    {
        SrcAccessMask = src,
        DstAccessMask = dst,
        OldLayout = from,
        NewLayout = to,
        SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        Image = image,
        SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
    };

    private static VkImageViewCreateInfo View(VkImage image, VkFormat format, VkImageAspectFlags aspect) => new()
    {
        Image = image,
        ViewType = VkImageViewType._2D,
        Format = format,
        SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
    };

    internal static VkShaderModule Shader(Device device, string source, ShaderKind kind, string name, bool vulkan13 = false)
    {
        var compiler = ShaderC.CreateCompiler();
        var options = ShaderC.CreateCompilerOptions();
        if (vulkan13)
        {
            options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3); // ray queries need SPIR-V 1.4 and later
            options.SetTargetSpirv(SpirvVersion._1_6);
        }

        using var result = compiler.Compile(Encoding.UTF8.GetBytes(source), kind, Utf8.GetBytes(name + "\0"), Utf8.GetBytes("main\0"), options);
        var module = device.CreateShaderModule(new VkShaderModuleCreateInfo { CodeSize = result.GetLength(), Code = (int*)result.GetBytes() }, null);
        options.Dispose();
        compiler.Dispose();
        return module;
    }

    // The scene's pipeline, or with overlay the splash's: no vertex buffer, no depth, alpha blended.
    internal static VkPipeline Pipeline(Device device, VkPipelineLayout layout, VkRenderPass renderPass, VkSampleCountFlags samples, VkShaderModule vertex, VkShaderModule fragment, bool overlay = false, bool vertexless = false, bool translucent = false)
    {
        var entry = "main"u8;
        fixed (byte* entryName = entry)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[]
            {
                new() { Stage = VkShaderStageFlags.VertexBit, Module = vertex, Name = entryName },
                new() { Stage = VkShaderStageFlags.FragmentBit, Module = fragment, Name = entryName },
            };
            var binding = new VkVertexInputBindingDescription { Binding = 0, Stride = ByteSize.Of<Vertex>(), InputRate = VkVertexInputRate.Vertex };
            var attributes = stackalloc VkVertexInputAttributeDescription[]
            {
                new() { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Zero },
                new() { Location = 1, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Of<float>(3) },
                new() { Location = 2, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Of<float>(6) },
                new() { Location = 3, Binding = 0, Format = VkFormat.R32UInt, Offset = ByteSize.Of<float>(9) },
                new() { Location = 4, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Of<float>(10) },
                new() { Location = 5, Binding = 0, Format = VkFormat.R32G32B32A32SFloat, Offset = ByteSize.Of<float>(13) },
            };
            var vertexInput = overlay || vertexless ? new VkPipelineVertexInputStateCreateInfo() : new VkPipelineVertexInputStateCreateInfo
            {
                VertexBindingDescriptionCount = 1,
                VertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 6,
                VertexAttributeDescriptions = attributes,
            };
            var assembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                PolygonMode = VkPolygonMode.Fill,
                CullMode = VkCullModeFlags.None,
                FrontFace = VkFrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = samples };
            var depth = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = !overlay, DepthWriteEnable = !overlay && !translucent, DepthCompareOp = VkCompareOp.Less };
            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = overlay || translucent, // translucent: blended over what is drawn, depth tested, not written
                SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
                DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = VkBlendFactor.One,
                DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
            };
            var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
            var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };

            VkPipeline pipeline;
            device.CreateGraphicsPipelines(
                default,
                [
                    new VkGraphicsPipelineCreateInfo
                    {
                        StageCount = 2,
                        Stages = stages,
                        VertexInputState = &vertexInput,
                        InputAssemblyState = &assembly,
                        ViewportState = &viewport,
                        RasterizationState = &raster,
                        MultisampleState = &multisample,
                        DepthStencilState = &depth,
                        ColorBlendState = &blend,
                        DynamicState = &dynamic,
                        Layout = layout,
                        RenderPass = renderPass,
                        Subpass = 0,
                    },
                ],
                default,
                new ReadOnlySpan<VkPipeline>(&pipeline, 1));
            return pipeline;
        }
    }
}
