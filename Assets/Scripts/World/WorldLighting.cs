using System;
using TerrariaMod.ID;
using UnityEngine;

namespace TerrariaMod.World
{
    /// <summary>
    /// Vanilla-aligned 2D RGB grid lighting + day/night cycle:
    /// - DayTime (0..54000 ticks @ 60Hz) and NightTime (0..32400 ticks @ 60Hz)
    /// - Sky sunlight propagation above SurfaceLayer through open air (no solid tile & no wall)
    /// - Point emitters (Torches, Furnaces, Hellforge, DemonAltar, Lava, held Torch)
    /// - 4-pass directional sweep (2 iterations) for smooth diagonal falloff.
    /// </summary>
    public class WorldLighting
    {
        public const double DayLength = 54000.0;
        public const double NightLength = 32400.0;
        public const float TicksPerSecond = 60f;

        private int _minX, _maxX, _minY, _maxY, _w, _h;
        private float[] _r = Array.Empty<float>();
        private float[] _g = Array.Empty<float>();
        private float[] _b = Array.Empty<float>();
        private float _lastAmbientQuantized = -1f;

        public float AmbientBrightness { get; private set; } = 1f;
        public Color AmbientSkyColor { get; private set; } = Color.white;
        public bool TimeStepDirty { get; set; }

        public void AdvanceTime(WorldMap world, float dt)
        {
            if (world == null) return;

            world.Time += dt * TicksPerSecond;
            if (world.DayTime)
            {
                if (world.Time >= DayLength)
                {
                    world.Time -= DayLength;
                    world.DayTime = false;
                }
            }
            else
            {
                if (world.Time >= NightLength)
                {
                    world.Time -= NightLength;
                    world.DayTime = true;
                }
            }

            AmbientSkyColor = EvaluateSkyColor(world, out float lum);
            AmbientBrightness = lum;

            float quantized = Mathf.Round(lum * 32f) / 32f;
            if (!Mathf.Approximately(quantized, _lastAmbientQuantized))
            {
                _lastAmbientQuantized = quantized;
                TimeStepDirty = true;
            }
        }

        public static Color EvaluateSkyColor(WorldMap world, out float luminance)
        {
            if (world == null)
            {
                luminance = 1f;
                return Color.white;
            }

            Color night = new Color(0.15f, 0.17f, 0.28f, 1f);
            Color dawn = new Color(0.88f, 0.68f, 0.56f, 1f);
            Color day = Color.white;
            Color dusk = new Color(0.92f, 0.58f, 0.42f, 1f);

            Color c;
            if (world.DayTime)
            {
                double t = world.Time;
                if (t < 5400.0)
                    c = Color.Lerp(night, dawn, (float)(t / 5400.0));
                else if (t < 10800.0)
                    c = Color.Lerp(dawn, day, (float)((t - 5400.0) / 5400.0));
                else if (t < 43200.0)
                    c = day;
                else if (t < 48600.0)
                    c = Color.Lerp(day, dusk, (float)((t - 43200.0) / 5400.0));
                else
                    c = Color.Lerp(dusk, night, (float)((t - 48600.0) / 5400.0));
            }
            else
            {
                c = night;
            }

            luminance = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            return c;
        }

        public static string FormatClock(WorldMap world)
        {
            if (world == null) return "12:00";
            // Terraria: Day 0 = 4:30 AM (270 min), Night 0 = 7:30 PM (1170 min)
            double totalMinutes = world.DayTime
                ? 270.0 + (world.Time / DayLength) * 900.0
                : 1170.0 + (world.Time / NightLength) * 540.0;
            int mins = ((int)totalMinutes) % 1440;
            int hh = mins / 60;
            int mm = mins % 60;
            return $"{(world.DayTime ? "昼" : "夜")} {hh:00}:{mm:00}";
        }

        public void ComputeRegion(WorldMap world, int minX, int maxX, int minY, int maxY, Vector2? playerLightPos, bool playerHoldingLight)
        {
            if (world == null) return;

            _minX = Math.Max(0, minX);
            _maxX = Math.Min(world.Width - 1, maxX);
            _minY = Math.Max(0, minY);
            _maxY = Math.Min(world.Height - 1, maxY);
            _w = _maxX - _minX + 1;
            _h = _maxY - _minY + 1;
            if (_w <= 0 || _h <= 0) return;

            int len = _w * _h;
            if (_r.Length < len)
            {
                _r = new float[len];
                _g = new float[len];
                _b = new float[len];
            }

            Color sky = EvaluateSkyColor(world, out _);
            double surface = world.SurfaceLayer > 10 ? world.SurfaceLayer : world.Height * 0.28;

            // 1. Seed sky sunlight & local emitters
            for (int y = _minY; y <= _maxY; y++)
            {
                int ly = y - _minY;
                bool aboveSurface = y <= surface + 4;
                for (int x = _minX; x <= _maxX; x++)
                {
                    int lx = x - _minX;
                    int li = ly * _w + lx;
                    int wi = world.Index(x, y);

                    float r = 0f, g = 0f, b = 0f;
                    bool active = (world.Flags[wi] & 1) != 0;
                    ushort tType = world.TileType[wi];
                    ushort wType = world.WallType[wi];
                    bool solid = active && TileRules.IsSolidForFraming(tType);

                    if (aboveSurface && !solid && wType == 0)
                    {
                        r = sky.r;
                        g = sky.g;
                        b = sky.b;
                    }

                    if (active)
                    {
                        if (tType == TileID.Torches)
                        {
                            r = Math.Max(r, 1.0f);
                            g = Math.Max(g, 0.88f);
                            b = Math.Max(b, 0.65f);
                        }
                        else if (tType == TileID.Furnaces || tType == TileID.Hellforge || tType == TileID.Campfire)
                        {
                            r = Math.Max(r, 1.0f);
                            g = Math.Max(g, 0.65f);
                            b = Math.Max(b, 0.30f);
                        }
                        else if (tType == TileID.DemonAltar)
                        {
                            r = Math.Max(r, 0.65f);
                            g = Math.Max(g, 0.35f);
                            b = Math.Max(b, 0.85f);
                        }
                    }

                    if (world.Liquid[wi] > 0 && world.LiquidType[wi] == 1) // Lava glow
                    {
                        r = Math.Max(r, 0.95f);
                        g = Math.Max(g, 0.42f);
                        b = Math.Max(b, 0.14f);
                    }

                    _r[li] = r;
                    _g[li] = g;
                    _b[li] = b;
                }
            }

            if (playerHoldingLight && playerLightPos.HasValue)
            {
                int px = WorldMap.WorldToTileX(playerLightPos.Value.x);
                int py = WorldMap.WorldToTileY(playerLightPos.Value.y);
                if (px >= _minX && px <= _maxX && py >= _minY && py <= _maxY)
                {
                    int li = (py - _minY) * _w + (px - _minX);
                    _r[li] = Math.Max(_r[li], 1.0f);
                    _g[li] = Math.Max(_g[li], 0.88f);
                    _b[li] = Math.Max(_b[li], 0.65f);
                }
            }

            // 2. 4-pass directional propagation (2 iterations for smooth diagonal corners)
            for (int iter = 0; iter < 2; iter++)
            {
                // Horizontal passes
                for (int ly = 0; ly < _h; ly++)
                {
                    int wy = _minY + ly;
                    float cr = 0f, cg = 0f, cb = 0f;
                    for (int lx = 0; lx < _w; lx++)
                        PropagateCell(world, _minX + lx, wy, ly * _w + lx, ref cr, ref cg, ref cb);

                    cr = cg = cb = 0f;
                    for (int lx = _w - 1; lx >= 0; lx--)
                        PropagateCell(world, _minX + lx, wy, ly * _w + lx, ref cr, ref cg, ref cb);
                }

                // Vertical passes
                for (int lx = 0; lx < _w; lx++)
                {
                    int wx = _minX + lx;
                    float cr = 0f, cg = 0f, cb = 0f;
                    for (int ly = 0; ly < _h; ly++)
                        PropagateCell(world, wx, _minY + ly, ly * _w + lx, ref cr, ref cg, ref cb);

                    cr = cg = cb = 0f;
                    for (int ly = _h - 1; ly >= 0; ly--)
                        PropagateCell(world, wx, _minY + ly, ly * _w + lx, ref cr, ref cg, ref cb);
                }
            }
        }

        private void PropagateCell(WorldMap world, int wx, int wy, int li, ref float cr, ref float cg, ref float cb)
        {
            if (cr > _r[li]) _r[li] = cr; else cr = _r[li];
            if (cg > _g[li]) _g[li] = cg; else cg = _g[li];
            if (cb > _b[li]) _b[li] = cb; else cb = _b[li];

            int wi = world.Index(wx, wy);
            bool active = (world.Flags[wi] & 1) != 0;
            bool solid = active && TileRules.IsSolidForFraming(world.TileType[wi]);
            float decay = solid ? 0.62f : 0.91f;

            cr *= decay;
            cg *= decay;
            cb *= decay;
        }

        public bool CoversTileRect(int minX, int maxX, int minY, int maxY)
        {
            return _w > 0 && _h > 0 && minX >= _minX && maxX <= _maxX && minY >= _minY && maxY <= _maxY;
        }

        public Color32 GetLight(int x, int y)
        {
            if (x < _minX || x > _maxX || y < _minY || y > _maxY || _w <= 0)
                return new Color32(0, 0, 0, 255);

            int li = (y - _minY) * _w + (x - _minX);
            byte r = (byte)(Mathf.Clamp01(_r[li]) * 255f);
            byte g = (byte)(Mathf.Clamp01(_g[li]) * 255f);
            byte b = (byte)(Mathf.Clamp01(_b[li]) * 255f);
            return new Color32(r, g, b, 255);
        }
    }
}
