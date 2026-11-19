using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace ISOCORE.ModSdk
{
    public enum DefaultSound { Pistol, Smg, Rifle, Sniper, Rocket, Explosion, EmptyClick }

    /// <summary>Creates sounds: built-in synthesized ones or custom audio files.</summary>
    public sealed class SoundApi
    {
        private readonly Assembly _assembly;
        private readonly string _modId;
        private readonly ManualLogSource _log;
        private readonly Dictionary<DefaultSound, AudioClip> _defaults =
            new Dictionary<DefaultSound, AudioClip>();
        private GameObject _root;

        /// <summary>
        /// Optional extra folder searched by Load() for relative paths. Normally not needed:
        /// sounds placed in the project's Assets/ folder are embedded in the mod DLL.
        /// </summary>
        public string Folder { get; set; }

        internal SoundApi(Assembly assembly, string modId, ManualLogSource log)
        {
            _assembly = assembly;
            _modId = modId;
            _log = log;
        }

        /// <summary>Persistent object that owns every AudioSource. Created lazily, rebuilt if destroyed.</summary>
        internal GameObject Root
        {
            get
            {
                if (_root == null)
                {
                    _root = new GameObject("ModSounds");
                    UnityEngine.Object.DontDestroyOnLoad(_root);
                }
                return _root;
            }
        }

        /// <summary>A built-in sound. Each call returns a new Sound (own volume/pitch), sharing the clip.</summary>
        public Sound Default(DefaultSound kind)
        {
            AudioClip clip;
            if (!_defaults.TryGetValue(kind, out clip) || clip == null)
                _defaults[kind] = clip = Synthesize(kind);
            return new Sound(this, clip);
        }

        /// <summary>Wrap an AudioClip you already have.</summary>
        public Sound FromClip(AudioClip clip) { return new Sound(this, clip); }

        /// <summary>
        /// Loads a .wav (immediately) or .ogg/.mp3 (asynchronously; the Sound is silent until loaded).
        /// The file is looked up in this order:
        ///   1. an absolute path, or Folder/path if Folder is set;
        ///   2. BepInEx/config/&lt;mod-id&gt;.Assets/path (lets players override sounds);
        ///   3. a sound embedded in the mod DLL from the project's Assets/ folder.
        /// </summary>
        public Sound Load(string path)
        {
            var sound = new Sound(this, null);
            if (string.IsNullOrEmpty(path)) return sound;
            try
            {
                string file = FindFile(path);
                byte[] embedded = file == null ? ReadEmbedded(path) : null;
                if (file == null && embedded == null)
                {
                    _log?.LogError("[Sound] Could not find '" + path + "'. Put it in the project's " +
                        "Assets/ folder (embedded in the DLL) or in BepInEx/config/" + _modId + ".Assets/.");
                    return sound;
                }

                string name = Path.GetFileNameWithoutExtension(path);
                if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    sound.Clip = ParseWav(name, file != null ? File.ReadAllBytes(file) : embedded);
                    return sound;
                }

                // Unity decodes .ogg/.mp3 from a file, so embedded data is written to a cache file first.
                if (file == null) file = WriteCacheFile(path, embedded);
                GameObject root = Root;
                SdkCoroutineHost host = root.GetComponent<SdkCoroutineHost>() ??
                    root.AddComponent<SdkCoroutineHost>();
                host.StartCoroutine(LoadWithUnity(file, sound));
            }
            catch (Exception e) { _log?.LogError("[Sound] Failed to load " + path + ": " + e.Message); }
            return sound;
        }

        private string FindFile(string path)
        {
            if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
            var folders = new List<string>();
            if (!string.IsNullOrEmpty(Folder)) folders.Add(Folder);
            folders.Add(Path.Combine(BepInEx.Paths.ConfigPath, _modId + ".Assets"));
            foreach (string folder in folders)
            {
                string candidate = Path.Combine(folder, path);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private byte[] ReadEmbedded(string path)
        {
            if (_assembly == null) return null;
            // Embedded resource names use dots for folders, e.g. "Guns.Assets.sfx.boom.wav".
            string suffix = path.Replace('\\', '.').Replace('/', '.');
            string match = null;
            foreach (string name in _assembly.GetManifestResourceNames())
            {
                bool exact = name.Equals(suffix, StringComparison.OrdinalIgnoreCase);
                if (!exact && !name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (exact || name.EndsWith(".Assets." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    match = name;
                    break;
                }
                if (match == null) match = name;
            }
            if (match == null) return null;
            using (Stream stream = _assembly.GetManifestResourceStream(match))
            {
                if (stream == null) return null;
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }

        private string WriteCacheFile(string path, byte[] data)
        {
            string directory = Path.Combine(Application.temporaryCachePath, "iccmf-sounds");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, _modId + "-" + Path.GetFileName(path));
            File.WriteAllBytes(file, data);
            return file;
        }

        private IEnumerator LoadWithUnity(string path, Sound sound)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            AudioType type = ext == ".ogg" ? AudioType.OGGVORBIS :
                ext == ".mp3" ? AudioType.MPEG : AudioType.UNKNOWN;
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type))
            {
                yield return request.SendWebRequest();
                if (!string.IsNullOrEmpty(request.error))
                {
                    _log?.LogError("[Sound] Failed to load " + path + ": " + request.error);
                    yield break;
                }
                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                clip.name = Path.GetFileNameWithoutExtension(path);
                sound.Clip = clip;
            }
        }

        // ---------- WAV parsing (PCM 8/16/24/32-bit and 32-bit float) ----------
        private static AudioClip ParseWav(string name, byte[] b)
        {
            if (b.Length < 44 || Ascii(b, 0, 4) != "RIFF" || Ascii(b, 8, 4) != "WAVE")
                throw new InvalidDataException("Not a WAV file.");
            int pos = 12, format = 0, channels = 0, rate = 0, bits = 0, dataStart = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = Ascii(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(b, body);
                    channels = BitConverter.ToUInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToUInt16(b, body + 14);
                    if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(b, body + 24);
                }
                else if (id == "data")
                {
                    dataStart = body;
                    dataLen = Math.Min(size, b.Length - body);
                    break;
                }
                pos = body + size + (size & 1);
            }
            if (dataStart < 0 || channels <= 0 || (format != 1 && format != 3))
                throw new InvalidDataException("Unsupported WAV format.");

            int bytes = bits / 8, count = dataLen / bytes;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                int o = dataStart + i * bytes;
                switch (bits)
                {
                    case 8: samples[i] = (b[o] - 128) / 128f; break;
                    case 16: samples[i] = BitConverter.ToInt16(b, o) / 32768f; break;
                    case 24:
                        int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
                        if (v >= 0x800000) v -= 0x1000000;
                        samples[i] = v / 8388608f; break;
                    case 32:
                        samples[i] = format == 3 ? BitConverter.ToSingle(b, o)
                            : BitConverter.ToInt32(b, o) / 2147483648f; break;
                    default: throw new InvalidDataException("Unsupported bit depth: " + bits);
                }
            }
            AudioClip clip = AudioClip.Create(name, count / channels, channels, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static string Ascii(byte[] b, int index, int length)
        { return System.Text.Encoding.ASCII.GetString(b, index, length); }

        // ---------- Built-in synthesized sounds ----------
        private static AudioClip Synthesize(DefaultSound kind)
        {
            switch (kind)
            {
                //                    dur   decay noise thumpHz thumpDecay thumpAmt lowpass gain
                case DefaultSound.Pistol:    return Build(kind, .35f, 18f, 1f, 140f, 30f, .5f, .60f, .9f);
                case DefaultSound.Smg:       return Build(kind, .20f, 30f, 1f, 170f, 40f, .4f, .70f, .8f);
                case DefaultSound.Rifle:     return Build(kind, .30f, 22f, 1f, 110f, 28f, .6f, .50f, .9f);
                case DefaultSound.Sniper:    return Build(kind, 1.0f, 7f, 1f, 70f, 10f, .9f, .30f, 1f);
                case DefaultSound.Rocket:    return Build(kind, .90f, 5f, 1f, 55f, 6f, .7f, .12f, .9f);
                case DefaultSound.Explosion: return Build(kind, 1.4f, 3.2f, 1f, 45f, 4f, 1f, .07f, 1f);
                default:                     return Build(kind, .06f, 90f, .3f, 1800f, 120f, .6f, .90f, .5f);
            }
        }

        private static AudioClip Build(DefaultSound kind, float duration, float decay, float noise,
            float thumpHz, float thumpDecay, float thumpAmount, float lowpass, float gain)
        {
            const int rate = 44100;
            int count = (int)(duration * rate);
            var data = new float[count];
            var rng = new System.Random((int)kind * 7919 + 13);
            float filtered = 0f, phase = 0f;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)rate;
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                filtered += lowpass * (white - filtered);                 // one-pole low-pass
                float attack = Mathf.Clamp01(t * 2000f);                  // avoids a click at t=0
                float body = filtered * noise * Mathf.Exp(-t * decay);
                float freq = Mathf.Lerp(thumpHz, thumpHz * .4f, Mathf.Clamp01(t * 8f));
                phase += 2f * Mathf.PI * freq / rate;
                float thump = Mathf.Sin(phase) * Mathf.Exp(-t * thumpDecay) * thumpAmount;
                data[i] = Mathf.Clamp((body * 2f + thump) * attack * gain, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create("default-" + kind, count, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }

    /// <summary>
    /// A playable sound. Play/Stop/Pause/Resume control the main voice; Fire() is for overlapping shots.
    /// AudioSources are created on first use, so constructing a Sound never touches the scene.
    /// </summary>
    public sealed class Sound
    {
        private const int FireVoices = 6;
        private readonly SoundApi _api;
        private readonly List<AudioSource> _fireVoices = new List<AudioSource>();
        private AudioSource _main;
        private AudioClip _clip;
        private int _nextVoice;
        private float _volume = 1f, _pitch = 1f;
        private bool _loop;

        internal Sound(SoundApi api, AudioClip clip)
        {
            _api = api;
            _clip = clip;
        }

        public AudioClip Clip
        {
            get { return _clip; }
            internal set { _clip = value; if (_main != null) _main.clip = value; }
        }
        public bool IsPlaying { get { return _main != null && _main.isPlaying; } }
        public bool IsPaused { get; private set; }

        public float Volume
        {
            get { return _volume; }
            set
            {
                _volume = Mathf.Clamp01(value);
                if (_main != null) _main.volume = _volume;
                foreach (AudioSource v in _fireVoices) if (v != null) v.volume = _volume;
            }
        }
        public float Pitch
        {
            get { return _pitch; }
            set { _pitch = value; if (_main != null) _main.pitch = value; }
        }
        public bool Loop
        {
            get { return _loop; }
            set { _loop = value; if (_main != null) _main.loop = value; }
        }

        /// <summary>Starts from the beginning (restarts if already playing).</summary>
        public void Play()
        {
            if (_clip == null) return;
            IsPaused = false;
            Main().Play();
        }

        public void Stop()
        {
            IsPaused = false;
            if (_main != null) _main.Stop();
            foreach (AudioSource v in _fireVoices) if (v != null) v.Stop();
        }

        public void Pause()
        {
            if (_main != null && _main.isPlaying) { _main.Pause(); IsPaused = true; }
        }

        public void Resume()
        {
            if (IsPaused && _main != null) { _main.UnPause(); IsPaused = false; }
        }

        /// <summary>
        /// Plays a fresh overlapping copy, so rapid fire doesn't cut itself off.
        /// pitchJitter = random +/- pitch variation (0.05 is subtle).
        /// </summary>
        public void Fire(float volumeScale = 1f, float pitchJitter = 0.04f)
        {
            if (_clip == null) return;
            AudioSource voice;
            if (_fireVoices.Count < FireVoices)
            {
                voice = NewSource();
                _fireVoices.Add(voice);
            }
            else
            {
                int index = _nextVoice++ % FireVoices;
                voice = _fireVoices[index];
                if (voice == null) _fireVoices[index] = voice = NewSource(); // was destroyed; rebuild
            }
            voice.clip = _clip;
            voice.volume = _volume * volumeScale;
            voice.pitch = _pitch + UnityEngine.Random.Range(-pitchJitter, pitchJitter);
            voice.Play();
        }

        private AudioSource Main()
        {
            if (_main == null)
            {
                _main = NewSource();
                _main.volume = _volume;
                _main.pitch = _pitch;
                _main.loop = _loop;
            }
            _main.clip = _clip;
            return _main;
        }

        private AudioSource NewSource()
        {
            var obj = new GameObject("Sound");
            obj.transform.SetParent(_api.Root.transform, false);
            AudioSource source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f; // 2D: always audible
            return source;
        }
    }
}