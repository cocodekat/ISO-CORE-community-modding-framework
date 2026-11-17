using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace ISOCORE.ModSdk
{
    /// <summary>Loads pixel-art textures from a mod's embedded Assets folder or config override folder.</summary>
    public sealed class AssetsApi
    {
        private const int MaxTextureBytes = 8 * 1024 * 1024;
        private readonly Assembly _assembly;
        private readonly ManualLogSource _log;
        private readonly string _overrideDirectory;
        private readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();

        internal AssetsApi(Assembly assembly, ManualLogSource log, string modId)
        {
            _assembly = assembly;
            _log = log;
            _overrideDirectory = Path.Combine(Paths.ConfigPath, modId + ".Assets");
        }

        /// <summary>
        /// Loads Assets/name.png. A same-named file in BepInEx/config/&lt;mod-id&gt;.Assets wins,
        /// making art replaceable without recompiling the mod.
        /// </summary>
        public Texture2D Texture(string name, int width = 0, int height = 0)
        {
            string key = Normalize(name);
            if (_textures.TryGetValue(key, out Texture2D cached)) return cached;

            byte[] bytes = ReadOverride(key) ?? ReadEmbedded(key);
            if (bytes == null) throw new FileNotFoundException("Missing mod texture: " + name);
            if (bytes.Length > MaxTextureBytes)
                throw new InvalidDataException("Texture exceeds the 8 MiB limit: " + name);

            var texture = new Texture2D(Math.Max(2, width), Math.Max(2, height),
                TextureFormat.RGBA32, false)
            {
                name = Path.GetFileNameWithoutExtension(key),
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!ImageConversion.LoadImage(texture, bytes, false))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException("Not a valid PNG texture: " + name);
            }
            if ((width > 0 && texture.width != width) || (height > 0 && texture.height != height))
            {
                int actualWidth = texture.width;
                int actualHeight = texture.height;
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException(name + " is " + actualWidth + "x" + actualHeight +
                    "; expected " + width + "x" + height + ".");
            }
            _textures.Add(key, texture);
            return texture;
        }

        public Sprite Sprite(string name, float pixelsPerUnit = 16f, Vector2? pivot = null,
            int width = 0, int height = 0)
        {
            Texture2D texture = Texture(name, width, height);
            Sprite sprite = UnityEngine.Sprite.Create(texture,
                new Rect(0f, 0f, texture.width, texture.height), pivot ?? new Vector2(.5f, .5f),
                pixelsPerUnit, 0, SpriteMeshType.FullRect);
            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        public Sprite[] SpriteSheet(string name, int frameWidth, int frameHeight,
            float pixelsPerUnit = 16f, Vector2? pivot = null)
        {
            Texture2D texture = Texture(name);
            if (frameWidth <= 0 || frameHeight <= 0 || texture.width % frameWidth != 0 ||
                texture.height % frameHeight != 0)
                throw new ArgumentException("Frame size must evenly divide the texture.");

            var sprites = new List<Sprite>();
            for (int y = texture.height - frameHeight; y >= 0; y -= frameHeight)
            for (int x = 0; x < texture.width; x += frameWidth)
            {
                Sprite sprite = UnityEngine.Sprite.Create(texture,
                    new Rect(x, y, frameWidth, frameHeight), pivot ?? new Vector2(.5f, .5f),
                    pixelsPerUnit, 0, SpriteMeshType.FullRect);
                sprite.name = texture.name + " " + sprites.Count;
                sprite.hideFlags = HideFlags.HideAndDontSave;
                sprites.Add(sprite);
            }
            return sprites.ToArray();
        }

        private byte[] ReadOverride(string key)
        {
            string path = Path.GetFullPath(Path.Combine(_overrideDirectory, key));
            string root = Path.GetFullPath(_overrideDirectory) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.Ordinal))
                throw new ArgumentException("Asset path must stay inside the mod asset folder.");
            if (!File.Exists(path)) return null;
            _log?.LogInfo("Loading texture override: " + path);
            return File.ReadAllBytes(path);
        }

        private byte[] ReadEmbedded(string key)
        {
            string suffix = ".Assets." + key.Replace('/', '.');
            string resource = _assembly.GetManifestResourceNames().FirstOrDefault(candidate =>
                candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (resource == null) return null;
            using (Stream input = _assembly.GetManifestResourceStream(resource))
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        private static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Asset name is required.");
            string value = name.Replace('\\', '/').TrimStart('/');
            if (!value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) value += ".png";
            return value;
        }
    }
}
