using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HorrorHouse.EditorTools
{
    /// <summary>
    /// Turns the downloaded recordings into game-ready clips.
    ///
    ///  * Import settings: footsteps and car passes decode on load (so they can be analysed and
    ///    start instantly), long ambience streams, and anything positional is mono.
    ///  * The light-switch recording holds an ON click and an OFF click in one file; it is cut
    ///    into two clips by finding the two loudest transients.
    ///  * Door recordings are trimmed of leading silence and faded out, so a door sound starts
    ///    the instant the door moves.
    ///
    /// Sources are in Assets/_Project/Audio/Recorded/Source; results go to .../Recorded/Slices.
    /// Licences and origins are in Recorded/SOURCES.txt.
    /// </summary>
    public static class RecordedAudioPrep
    {
        public const string RecDir = "Assets/_Project/Audio/Recorded";
        const string SrcDir = RecDir + "/Source";
        public const string SliceDir = RecDir + "/Slices";

        [MenuItem("Horror House/Prepare Recorded Audio", priority = 75)]
        public static void Run()
        {
            Directory.CreateDirectory(SliceDir);
            AssetDatabase.Refresh();
            Step("import settings", ConfigureImports);
            Step("switch clicks", SliceSwitch);
            Step("doors", PrepareDoors);
            AssetDatabase.Refresh();
            Debug.Log("[RecordedAudio] Done.");
        }

        static void Step(string name, System.Action a)
        {
            try { a(); Debug.Log($"[RecordedAudio] {name}: ok"); }
            catch (System.Exception e) { Debug.LogError($"[RecordedAudio] '{name}' failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }

        // ------------------------------------------------------------ imports

        static void ConfigureImports()
        {
            foreach (var p in Directory.GetFiles(RecDir, "*.*", SearchOption.AllDirectories)
                         .Select(f => f.Replace('\\', '/'))
                         .Where(f => f.EndsWith(".mp3") || f.EndsWith(".ogg") || f.EndsWith(".wav")))
            {
                bool footstep = p.Contains("/Footsteps/");
                bool car = p.Contains("/Cars/");
                bool source = p.Contains("/Source/");
                bool slice = p.Contains("/Slices/");
                string file = Path.GetFileName(p);

                bool longLoop = file.StartsWith("crickets") || file.StartsWith("wind") || file.StartsWith("pink");
                bool mono = footstep || car || slice || file.StartsWith("fridge") || file.StartsWith("pink");
                var load = longLoop ? AudioClipLoadType.Streaming
                         : (footstep || car || source || slice) ? AudioClipLoadType.DecompressOnLoad
                         : AudioClipLoadType.CompressedInMemory;

                var imp = AssetImporter.GetAtPath(p) as AudioImporter;
                if (imp == null) continue;
                var s = imp.defaultSampleSettings;
                bool dirty = imp.forceToMono != mono || s.loadType != load || s.preloadAudioData == longLoop;
                if (!dirty) continue;

                imp.forceToMono = mono;
                s.preloadAudioData = !longLoop;
                s.loadType = load;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.75f;
                imp.defaultSampleSettings = s;
                imp.SaveAndReimport();
            }
        }

        // ------------------------------------------------------------ switch

        static void SliceSwitch()
        {
            var clip = Load("light_switch_0026.mp3");
            Decode(clip, out var mono, out int rate);

            var onsets = Onsets(mono, rate, 2, 0.25f);
            if (onsets.Count < 2)
            {
                Debug.LogWarning($"[RecordedAudio] Found {onsets.Count} click(s) in the switch recording; using the first for both.");
                if (onsets.Count == 0) onsets.Add(0);
                onsets.Add(onsets[0]);
            }
            Cut(mono, rate, onsets[0] - (int)(0.008f * rate), 0.24f, 0.03f, $"{SliceDir}/switch_on.wav");
            Cut(mono, rate, onsets[1] - (int)(0.008f * rate), 0.24f, 0.03f, $"{SliceDir}/switch_off.wav");
            Debug.Log($"[RecordedAudio] Switch clicks at {onsets[0] / (float)rate:F2}s and {onsets[1] / (float)rate:F2}s.");
        }

        // ------------------------------------------------------------ doors

        static void PrepareDoors()
        {
            TrimAndSave("door_open_1702.mp3", "door_open.wav", 3.4f);
            TrimAndSave("door_close_1704.mp3", "door_close.wav", 2.6f);
            TrimAndSave("door_lock_0037.mp3", "door_locked.wav", 1.8f);
        }

        static void TrimAndSave(string source, string dest, float maxLen)
        {
            var clip = Load(source);
            Decode(clip, out var mono, out int rate);

            float peak = mono.Max(v => Mathf.Abs(v));
            float thr = peak * 0.04f;
            int first = 0;
            while (first < mono.Length && Mathf.Abs(mono[first]) < thr) first++;
            first = Mathf.Max(0, first - (int)(0.02f * rate));

            int last = mono.Length - 1;
            while (last > first && Mathf.Abs(mono[last]) < thr * 0.5f) last--;
            last = Mathf.Min(mono.Length - 1, last + (int)(0.15f * rate));

            float len = Mathf.Min(maxLen, (last - first) / (float)rate);
            Cut(mono, rate, first, len, 0.12f, $"{SliceDir}/{dest}");
        }

        // ------------------------------------------------------------ analysis + output

        static AudioClip Load(string file)
        {
            var c = AssetDatabase.LoadAssetAtPath<AudioClip>($"{SrcDir}/{file}");
            if (c == null) throw new FileNotFoundException($"{SrcDir}/{file} is not imported as an AudioClip");
            return c;
        }

        static void Decode(AudioClip clip, out float[] mono, out int rate)
        {
            var data = new float[clip.samples * clip.channels];
            if (!clip.GetData(data, 0)) throw new System.Exception($"{clip.name}: cannot read samples (is it set to Decompress On Load?)");
            mono = new float[clip.samples];
            for (int i = 0; i < mono.Length; i++)
            {
                float s = 0f;
                for (int c = 0; c < clip.channels; c++) s += data[i * clip.channels + c];
                mono[i] = s / clip.channels;
            }
            rate = clip.frequency;
        }

        /// <summary>Sample indices of the strongest <paramref name="count"/> transients, earliest first.</summary>
        static List<int> Onsets(float[] x, int rate, int count, float minGapSec)
        {
            int hop = Mathf.Max(1, (int)(0.003f * rate));
            int n = x.Length / hop;
            var env = new float[n];
            for (int i = 0; i < n; i++)
            {
                float e = 0f;
                for (int j = 0; j < hop; j++) { float v = x[i * hop + j]; e += v * v; }
                env[i] = Mathf.Sqrt(e / hop);
            }
            float peak = env.Max();
            float floor = env.OrderBy(v => v).ElementAt(n / 5);
            float thr = Mathf.Max(peak * 0.22f, floor * 5f);

            var cands = new List<(int idx, float strength)>();
            for (int i = 1; i < n; i++)
            {
                if (env[i] < thr || env[i - 1] >= thr) continue;           // rising edge only
                float s = 0f;
                for (int k = i; k < Mathf.Min(n, i + 30); k++) s = Mathf.Max(s, env[k]);
                cands.Add((i * hop, s));
            }

            var picked = new List<(int idx, float strength)>();
            foreach (var c in cands.OrderByDescending(c => c.strength))
            {
                if (picked.Any(p => Mathf.Abs(p.idx - c.idx) < minGapSec * rate)) continue;
                picked.Add(c);
                if (picked.Count == count) break;
            }
            return picked.OrderBy(p => p.idx).Select(p => p.idx).ToList();
        }

        static void Cut(float[] x, int rate, int start, float seconds, float fadeOut, string path)
        {
            start = Mathf.Clamp(start, 0, x.Length - 1);
            int len = Mathf.Min((int)(seconds * rate), x.Length - start);
            var o = new float[len];
            System.Array.Copy(x, start, o, 0, len);

            int fade = Mathf.Min((int)(fadeOut * rate), len);
            for (int i = 0; i < fade; i++) o[len - 1 - i] *= (float)i / fade;
            int fin = Mathf.Min((int)(0.002f * rate), len);
            for (int i = 0; i < fin; i++) o[i] *= (float)i / fin;

            float peak = o.Max(v => Mathf.Abs(v));
            if (peak > 1e-5f) { float k = 0.85f / peak; for (int i = 0; i < len; i++) o[i] *= k; }
            WriteWav(path, o, rate);
        }

        static void WriteWav(string path, float[] samples, int rate)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            int bytes = samples.Length * 2;
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + bytes);
            bw.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(rate);
            bw.Write(rate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(bytes);
            foreach (var s in samples) bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
        }
    }
}
