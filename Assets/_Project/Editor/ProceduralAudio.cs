using System.IO;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Synthesises every sound the house and street need, as 16-bit mono WAV files.
    ///
    /// There is no audio in the project, and sourcing dozens of licensed recordings is slow and
    /// leaves attribution to track. These are built from noise, resonant filters and decaying
    /// sines instead: a cricket is a train of ~11 ms pulses on a ~4.5 kHz carrier, a footstep is a
    /// low thump plus a band-limited noise burst whose colour depends on the surface, a fridge is
    /// mains hum (50 Hz in NZ) and its harmonics. Everything is deterministic (fixed seeds), so
    /// regenerating gives byte-identical files and clean git diffs.
    ///
    /// They are good, not perfect. Any clip can be replaced with a real recording later: the
    /// runtime scripts only ever reference the AudioClip, never how it was made.
    /// </summary>
    public static class ProceduralAudio
    {
        const int Rate = 44100;
        public const string Dir = "Assets/_Project/Audio/Generated";

        public static readonly string[] Surfaces = { "concrete", "asphalt", "grass", "wood", "tile", "carpet" };
        public const int Variants = 4;

        [MenuItem("Horror House/Generate Audio", priority = 72)]
        public static void Generate()
        {
            Directory.CreateDirectory(Dir);

            Write("cricket_loop", Crickets(24f));
            Write("night_static", Static(10f));
            Write("wind_loop", Wind(12f));
            Write("fridge_hum", FridgeHum(6f));
            Write("car_pass_loop", CarEngine(4f));

            Write("door_open", DoorOpen());
            Write("door_close", DoorClose());
            Write("door_locked", DoorLocked());
            Write("switch_on", Switch(true));
            Write("switch_off", Switch(false));

            for (int s = 0; s < Surfaces.Length; s++)
                for (int v = 0; v < Variants; v++)
                    Write($"step_{Surfaces[s]}_{v}", Step(Surfaces[s], s * 10 + v, v));

            AssetDatabase.Refresh();
            Debug.Log($"[Audio] Generated clips in {Dir}");
        }

        // ============================================================ ambience

        /// <summary>
        /// Several crickets, each a different pitch and rhythm, so they never sync into one
        /// mechanical buzz. Chirps wrap around the buffer end, so the loop has no seam.
        /// </summary>
        static float[] Crickets(float seconds)
        {
            int n = (int)(seconds * Rate);
            var o = new float[n];
            var r = new System.Random(1021);

            for (int c = 0; c < 8; c++)
            {
                float f = 3900f + 1500f * (float)r.NextDouble();
                float pulseRate = 25f + 9f * (float)r.NextDouble();
                int pulses = 3 + r.Next(3);
                float gap = 0.35f + 0.55f * (float)r.NextDouble();
                float gain = 0.09f + 0.08f * (float)r.NextDouble();
                float t = (float)r.NextDouble() * gap;

                while (t < seconds)
                {
                    for (int p = 0; p < pulses; p++)
                    {
                        int s0 = (int)((t + p / pulseRate) * Rate);
                        int len = (int)(0.011f * Rate);
                        float amp = gain * (1f - 0.06f * p);
                        for (int i = 0; i < len; i++)
                        {
                            float env = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * i / len);
                            float ph = 2f * Mathf.PI * f * i / Rate;
                            o[(s0 + i) % n] += amp * env * (Mathf.Sin(ph) + 0.25f * Mathf.Sin(2f * ph));
                        }
                    }
                    t += gap * (0.85f + 0.3f * (float)r.NextDouble());
                }
            }

            var bed = Noise(n, r);
            BandPass(bed, 2500f, 6500f);
            for (int i = 0; i < n; i++) o[i] += bed[i] * 0.004f;

            Normalize(o, 0.6f);
            return o;
        }

        /// <summary>A quiet bed of band-limited noise with mains hum and the odd crackle: old-TV static.</summary>
        static float[] Static(float seconds)
        {
            int n = (int)(seconds * Rate);
            var r = new System.Random(77);
            var x = Noise(n, r);
            BandPass(x, 180f, 7500f);

            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float breathe = 0.85f + 0.15f * Mathf.Sin(2f * Mathf.PI * 0.3f * t);
                x[i] = x[i] * 0.16f * breathe
                     + 0.018f * Mathf.Sin(2f * Mathf.PI * 50f * t)
                     + 0.007f * Mathf.Sin(2f * Mathf.PI * 100f * t);
            }

            // Occasional crackle: a few ms of bright noise.
            float at = 0.3f;
            while (at < seconds - 0.1f)
            {
                int s0 = (int)(at * Rate);
                int len = (int)((0.002f + 0.004f * (float)r.NextDouble()) * Rate);
                float amp = 0.15f + 0.3f * (float)r.NextDouble();
                for (int i = 0; i < len && s0 + i < n; i++)
                    x[s0 + i] += amp * (float)(r.NextDouble() * 2 - 1) * (1f - (float)i / len);
                at += 0.5f + 2.5f * (float)r.NextDouble();
            }

            x = MakeLoop(x, (int)(0.5f * Rate));
            Normalize(x, 0.35f);
            return x;
        }

        static float[] Wind(float seconds)
        {
            int n = (int)(seconds * Rate);
            var r = new System.Random(5);
            var x = Noise(n, r);
            LowPass(x, 500f);
            LowPass(x, 500f);
            var gust = Noise(n, r);
            LowPass(gust, 0.25f);
            LowPass(gust, 0.25f);
            Normalize(gust, 1f);
            for (int i = 0; i < n; i++) x[i] *= 0.55f + 0.45f * (gust[i] * 0.5f + 0.5f);
            x = MakeLoop(x, (int)(1.5f * Rate));
            Normalize(x, 0.3f);
            return x;
        }

        static float[] FridgeHum(float seconds)
        {
            int n = (int)(seconds * Rate);
            var r = new System.Random(50);
            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float v = 0.50f * Mathf.Sin(2f * Mathf.PI * 50f * t)
                        + 0.30f * Mathf.Sin(2f * Mathf.PI * 100f * t + 0.5f)
                        + 0.17f * Mathf.Sin(2f * Mathf.PI * 150f * t + 1.1f)
                        + 0.09f * Mathf.Sin(2f * Mathf.PI * 200f * t + 2.0f)
                        + 0.05f * Mathf.Sin(2f * Mathf.PI * 300f * t);
                // 0.5 Hz wobble over a 6 s loop is exactly 3 cycles, so it still loops cleanly.
                o[i] = v * (1f + 0.10f * Mathf.Sin(2f * Mathf.PI * 0.5f * t));
            }
            var bed = Noise(n, r);
            BandPass(bed, 120f, 700f);
            for (int i = 0; i < n; i++) o[i] += bed[i] * 0.08f;
            Normalize(o, 0.5f);
            return o;
        }

        static float[] CarEngine(float seconds)
        {
            int n = (int)(seconds * Rate);
            var r = new System.Random(48);
            var o = new float[n];
            var phase = new float[14];
            for (int h = 0; h < phase.Length; h++) phase[h] = (float)r.NextDouble() * 6.2832f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float v = 0f;
                for (int h = 1; h < phase.Length; h++)
                    v += Mathf.Sin(2f * Mathf.PI * 48f * h * t + phase[h]) / (h * 0.9f);
                // 6 Hz lumpiness: 24 whole cycles over 4 s.
                o[i] = v * (1f + 0.22f * Mathf.Sin(2f * Mathf.PI * 6f * t));
            }
            LowPass(o, 900f);
            var road = Noise(n, r);
            BandPass(road, 250f, 1400f);
            for (int i = 0; i < n; i++) o[i] = o[i] * 0.55f + road[i] * 0.35f;
            Normalize(o, 0.6f);
            return o;
        }

        // ============================================================ doors + switches

        static float[] DoorOpen()
        {
            var r = new System.Random(901);
            int n = (int)(1.1f * Rate);
            var o = new float[n];
            Click(o, 0, 0.5f, 1900f, 3100f);                 // latch releasing

            // Creak: a sawtooth whose pitch wanders, chopped by a stick-slip envelope.
            var slip = Noise(n, r);
            LowPass(slip, 16f);
            Normalize(slip, 1f);
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                if (t < 0.06f) continue;
                float f = 150f + 70f * Mathf.Sin(2f * Mathf.PI * 1.4f * t) + 25f * slip[i];
                phase += f / Rate;
                float saw = 2f * (phase - Mathf.Floor(phase)) - 1f;
                float gate = Mathf.Clamp01(slip[i] * 2.2f + 0.5f);
                float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01((t - 0.06f) / 1.0f));
                o[i] += saw * gate * env * 0.5f;
            }
            BandPass(o, 220f, 1600f);
            Resonate(o, 640f, 3f, 0.6f);
            FadeEdges(o, 0.01f);
            Normalize(o, 0.75f);
            return o;
        }

        static float[] DoorClose()
        {
            var r = new System.Random(902);
            int n = (int)(0.7f * Rate);
            var o = new float[n];
            Thump(o, 0, 0.9f, 62f, 0.16f);                   // slab meeting frame
            Thump(o, 0, 0.45f, 135f, 0.07f);
            NoiseBurst(o, 0, 0.05f, 150f, 1200f, 0.5f, 0f, r);
            Click(o, (int)(0.045f * Rate), 0.35f, 1700f, 2900f);   // latch catching
            FadeEdges(o, 0.005f);
            Normalize(o, 0.8f);
            return o;
        }

        static float[] DoorLocked()
        {
            int n = (int)(0.55f * Rate);
            var o = new float[n];
            Click(o, 0, 0.7f, 2100f, 3400f);
            Click(o, (int)(0.09f * Rate), 0.55f, 1900f, 3000f);
            Click(o, (int)(0.2f * Rate), 0.4f, 2300f, 3600f);
            Thump(o, (int)(0.2f * Rate), 0.3f, 95f, 0.05f);
            FadeEdges(o, 0.004f);
            Normalize(o, 0.7f);
            return o;
        }

        static float[] Switch(bool on)
        {
            int n = (int)(0.18f * Rate);
            var o = new float[n];
            float k = on ? 1.12f : 0.92f;
            Click(o, 0, 0.8f, 2800f * k, 4300f * k);
            Click(o, (int)(0.011f * Rate), 0.45f, 1300f * k, 2100f * k);
            FadeEdges(o, 0.003f);
            Normalize(o, 0.7f);
            return o;
        }

        // ============================================================ footsteps

        static float[] Step(string surface, int seed, int variant)
        {
            var r = new System.Random(3000 + seed);
            int n = (int)(0.42f * Rate);
            var o = new float[n];
            float pitch = 1f + 0.08f * (variant - 1.5f);

            switch (surface)
            {
                case "concrete":
                    Thump(o, 0, 0.55f, 95f * pitch, 0.07f);
                    NoiseBurst(o, 0, 0.012f, 1800f, 5200f, 0.9f, 0f, r);
                    NoiseBurst(o, (int)(0.012f * Rate), 0.07f, 1400f, 4200f, 0.16f, 0.01f, r);
                    break;
                case "asphalt":
                    Thump(o, 0, 0.5f, 85f * pitch, 0.08f);
                    Grit(o, 0, 0.11f, 1200f, 3800f, 0.55f, r);
                    break;
                case "grass":
                    NoiseBurst(o, 0, 0.22f, 700f, 3200f, 0.55f, 0.035f, r);
                    Thump(o, 0, 0.35f, 70f * pitch, 0.09f);
                    break;
                case "wood":
                    Thump(o, 0, 0.8f, 118f * pitch, 0.11f);
                    Thump(o, 0, 0.35f, 236f * pitch, 0.06f);
                    NoiseBurst(o, 0, 0.01f, 900f, 3000f, 0.45f, 0f, r);
                    if (variant == 3) Creak(o, (int)(0.03f * Rate), 0.28f, 700f, 930f);
                    break;
                case "tile":
                    NoiseBurst(o, 0, 0.006f, 2500f, 7500f, 1.0f, 0f, r);
                    Ring(o, 0, 0.4f, 1900f + 60f * variant, 0.05f);
                    Thump(o, 0, 0.25f, 110f * pitch, 0.05f);
                    break;
                default: // carpet
                    Thump(o, 0, 0.45f, 70f * pitch, 0.07f);
                    NoiseBurst(o, 0, 0.12f, 300f, 1200f, 0.22f, 0.025f, r);
                    break;
            }
            FadeEdges(o, 0.004f);
            Normalize(o, 0.8f);
            return o;
        }

        // ============================================================ building blocks

        /// <summary>A sharp transient rung through two resonances: a latch, a switch, a key.</summary>
        static void Click(float[] dst, int at, float amp, float f1, float f2)
        {
            int len = (int)(0.06f * Rate);
            var b = new float[len];
            b[0] = 1f;
            var c = new float[len];
            b.CopyTo(c, 0);
            Resonate(b, f1, 14f, 1f);
            Resonate(c, f2, 9f, 1f);
            for (int i = 0; i < len && at + i < dst.Length; i++)
                dst[at + i] += amp * (b[i] + 0.7f * c[i]) * Mathf.Exp(-i / (0.006f * Rate));
        }

        static void Thump(float[] dst, int at, float amp, float freq, float decay)
        {
            int len = (int)(decay * 6f * Rate);
            for (int i = 0; i < len && at + i < dst.Length; i++)
            {
                float t = (float)i / Rate;
                float f = freq * (1f + 0.35f * Mathf.Exp(-t / 0.02f));   // pitch drops as it hits
                dst[at + i] += amp * Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t / decay);
            }
        }

        static void Ring(float[] dst, int at, float amp, float freq, float decay)
        {
            int len = (int)(decay * 6f * Rate);
            for (int i = 0; i < len && at + i < dst.Length; i++)
            {
                float t = (float)i / Rate;
                dst[at + i] += amp * Mathf.Sin(2f * Mathf.PI * freq * t) * Mathf.Exp(-t / decay);
            }
        }

        static void NoiseBurst(float[] dst, int at, float dur, float lo, float hi, float amp, float attack, System.Random r)
        {
            int len = (int)(dur * Rate);
            var b = Noise(len, r);
            BandPass(b, lo, hi);
            Normalize(b, 1f);
            int att = (int)(attack * Rate);
            for (int i = 0; i < len && at + i < dst.Length; i++)
            {
                float env = i < att ? (float)i / Mathf.Max(1, att) : Mathf.Exp(-(i - att) / (0.35f * len));
                dst[at + i] += amp * b[i] * env;
            }
        }

        /// <summary>Scattered short impulses: gravel, grit, a tyre on rough tarmac.</summary>
        static void Grit(float[] dst, int at, float dur, float lo, float hi, float amp, System.Random r)
        {
            int len = (int)(dur * Rate);
            for (int g = 0; g < 18; g++)
            {
                int p = at + (int)(r.NextDouble() * len);
                var b = Noise((int)(0.003f * Rate), r);
                BandPass(b, lo, hi);
                float a = amp * (0.3f + 0.7f * (float)r.NextDouble());
                for (int i = 0; i < b.Length && p + i < dst.Length; i++)
                    dst[p + i] += a * b[i] * (1f - (float)i / b.Length);
            }
        }

        static void Creak(float[] dst, int at, float amp, float f0, float f1)
        {
            int len = (int)(0.16f * Rate);
            float phase = 0f;
            for (int i = 0; i < len && at + i < dst.Length; i++)
            {
                float u = (float)i / len;
                phase += Mathf.Lerp(f0, f1, u) / Rate;
                dst[at + i] += amp * Mathf.Sin(2f * Mathf.PI * phase) * Mathf.Sin(Mathf.PI * u);
            }
        }

        // ============================================================ DSP

        static float[] Noise(int n, System.Random r)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = (float)(r.NextDouble() * 2.0 - 1.0);
            return a;
        }

        static void LowPass(float[] x, float cutoff)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff / Rate);
            float y = 0f;
            for (int i = 0; i < x.Length; i++) { y += a * (x[i] - y); x[i] = y; }
        }

        static void HighPass(float[] x, float cutoff)
        {
            var lp = (float[])x.Clone();
            LowPass(lp, cutoff);
            for (int i = 0; i < x.Length; i++) x[i] -= lp[i];
        }

        static void BandPass(float[] x, float lo, float hi)
        {
            HighPass(x, lo);
            LowPass(x, hi);
        }

        /// <summary>RBJ band-pass biquad. 'mix' blends the resonance back over the dry signal.</summary>
        static void Resonate(float[] x, float freq, float q, float mix)
        {
            float w0 = 2f * Mathf.PI * freq / Rate;
            float alpha = Mathf.Sin(w0) / (2f * q);
            float a0 = 1f + alpha;
            float b0 = alpha / a0, b2 = -alpha / a0;
            float a1 = -2f * Mathf.Cos(w0) / a0, a2 = (1f - alpha) / a0;
            float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                float xi = x[i];
                float y = b0 * xi + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = xi; y2 = y1; y1 = y;
                x[i] = Mathf.Lerp(xi, y * q, mix);
            }
        }

        static float[] MakeLoop(float[] x, int xf)
        {
            int n = x.Length - xf;
            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (i < xf)
                {
                    float w = (float)i / xf;
                    o[i] = x[i] * Mathf.Sqrt(w) + x[n + i] * Mathf.Sqrt(1f - w);   // equal-power
                }
                else o[i] = x[i];
            }
            return o;
        }

        static void Normalize(float[] x, float peak)
        {
            float m = 0f;
            for (int i = 0; i < x.Length; i++) m = Mathf.Max(m, Mathf.Abs(x[i]));
            if (m < 1e-6f) return;
            float k = peak / m;
            for (int i = 0; i < x.Length; i++) x[i] *= k;
        }

        static void FadeEdges(float[] x, float seconds)
        {
            int f = Mathf.Min((int)(seconds * Rate), x.Length / 2);
            for (int i = 0; i < f; i++)
            {
                float w = (float)i / f;
                x[i] *= w;
                x[x.Length - 1 - i] *= w;
            }
        }

        // ============================================================ WAV

        static void Write(string name, float[] samples)
        {
            string path = $"{Dir}/{name}.wav";
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            int dataBytes = samples.Length * 2;
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataBytes);
            bw.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            bw.Write(16);
            bw.Write((short)1);          // PCM
            bw.Write((short)1);          // mono
            bw.Write(Rate);
            bw.Write(Rate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(dataBytes);
            for (int i = 0; i < samples.Length; i++)
                bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), -32768, 32767));
        }
    }
}
