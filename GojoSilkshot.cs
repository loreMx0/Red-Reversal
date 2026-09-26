using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using InControl;
using UnityEngine;
using UnityEngine.Networking;

namespace SilksongRED;

[BepInPlugin("com.yourname.gojosilkshot", "Gojo Silkshot", "3.2.1")]
public class GojoSilkshot : BaseUnityPlugin
{
    private GameObject hornetHero;
    private AudioSource modAudioSource;
    private AudioClip clipCharge;
    private AudioClip clipFire;
    private Texture2D texBody;
    private Texture2D texEffects;

    private const string ANTIC_ANIM = "WebShot Antic W";
    private const string FIRE_ANIM = "WebShot Fire";

    private bool isCharging = false;
    private float chargeTimer = 0f;
    private bool fireQueued = false;
    private bool texturesLoaded = false;
    private bool hasFrozen = false;
    private bool waitingForRelease = false;

    private const float MAX_CHARGE_TIME = 3f;
    private const float AUTO_FIRE_TIME = 6f;
    private const float MAX_DAMAGE_MULT = 5f;

    // ---------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------

    private void Awake()
    {
        try
        {
            Logger.LogInfo("GojoSilkshot 3.2.1 loaded");

            StartCoroutine(LoadAudio("realred.wav", c => clipCharge = c));
            StartCoroutine(LoadAudio("red_fire.wav", c => clipFire = c));

            StartCoroutine(LoadTexture("red_body.png", body =>
            {
                texBody = body;
                StartCoroutine(LoadTexture("red_effects.png", fx =>
                {
                    texEffects = fx;
                    texturesLoaded = true;
                    Logger.LogInfo("Textures loaded");
                }));
            }));
        }
        catch (Exception ex)
        {
            Logger.LogError("Awake failed: " + ex);
        }
    }

    private void Update()
    {
        try
        {
            if (hornetHero == null)
            {
                hornetHero = GameObject.Find("Hero_Hornet(Clone)");
                if (hornetHero != null && modAudioSource == null)
                {
                    modAudioSource = hornetHero.AddComponent<AudioSource>();
                    modAudioSource.volume = 1f;
                    modAudioSource.spatialBlend = 0f;
                }
                return;
            }

            bool held = GetInputHeld();

            if (waitingForRelease)
            {
                if (!held) waitingForRelease = false;
                return;
            }

            if (held && !isCharging && !fireQueued)
            {
                isCharging = true;
                chargeTimer = 0f;
                fireQueued = false;
                hasFrozen = false;

                var toolHornet = hornetHero.transform.Find("Tool Effects/Tool Hornet");
                if (toolHornet != null)
                {
                    var anim = toolHornet.GetComponent<tk2dSpriteAnimator>();
                    if (anim != null)
                    {
                        anim.Play(ANTIC_ANIM);
                        anim.ClipFps = 12f;
                    }
                }

                StartChargeAudio();
            }

            if (isCharging)
            {
                chargeTimer += Time.deltaTime;

                if (chargeTimer >= AUTO_FIRE_TIME)
                {
                    isCharging = false;
                    FireNow();
                    if (held) waitingForRelease = true;
                    EnsureAudioStopped();
                    return;
                }

                if (!held)
                {
                    isCharging = false;
                    if (chargeTimer > 0.1f)
                    {
                        FireNow();
                    }
                    else
                    {
                        fireQueued = true;
                        ResetFPS();
                    }
                    EnsureAudioStopped();
                }
            }

            if (fireQueued) CheckQueue();

            // Safety net — no charge sound should ever be playing while not charging.
            if (!isCharging) EnsureAudioStopped();
        }
        catch (Exception ex)
        {
            Logger.LogError("Update failed: " + ex);
        }
    }

    private void LateUpdate()
    {
        try
        {
            if (hornetHero == null || !texturesLoaded) return;

            if (isCharging && !fireQueued)
            {
                var toolHornet = hornetHero.transform.Find("Tool Effects/Tool Hornet");
                if (toolHornet != null)
                {
                    var anim = toolHornet.GetComponent<tk2dSpriteAnimator>();
                    if (anim != null)
                    {
                        if (anim.IsPlaying(ANTIC_ANIM) && anim.CurrentFrame >= 3)
                        {
                            anim.Stop();
                            hasFrozen = true;
                        }
                        if (hasFrozen && anim.CurrentClip != null && anim.CurrentClip.name == ANTIC_ANIM)
                            anim.SetFrame(3);
                    }
                }
            }

            ApplyTex("Tool Effects/Tool Hornet", texBody);
            ApplyTex("Tool Effects/WebShot Effects/Gun Sprite", texBody);

            var heroRenderer = hornetHero.GetComponent<Renderer>();
            var heroAnim = hornetHero.GetComponent<tk2dSpriteAnimator>();
            if (heroRenderer != null && heroAnim != null && heroAnim.CurrentClip != null &&
                (heroAnim.CurrentClip.name.Contains("Cast") || heroAnim.CurrentClip.name.Contains("WebShot")) &&
                heroRenderer.material.mainTexture != texBody)
            {
                heroRenderer.material.mainTexture = texBody;
            }

            ApplyTex("Tool Effects/WebShot Effects/Spit Effect W", texEffects);

            var impactW = hornetHero.transform.Find("Tool Effects/WebShot Effects/SnipeShot Impact/Impact W");
            if (impactW != null)
            {
                foreach (var r in impactW.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.material.mainTexture != null && r.material.mainTexture != texEffects)
                        r.material.mainTexture = texEffects;
                }
            }

            ApplyTint("Tool Effects/WebShot Effects/WebShot_antic_effect/WebShot_antic_effect");
            ApplyTint("Tool Effects/WebShot Effects/SnipeShot Impact/SnipeShot Trail/Sprite");
            ApplyTint("Tool Effects/WebShot Effects/SnipeShot Impact/Impact W/web_shot_rail");
            ApplyTint("Tool Effects/WebShot Effects/SnipeShot Impact/Impact W/web_shot_sphere");
        }
        catch (Exception ex)
        {
            Logger.LogError("LateUpdate failed: " + ex);
        }
    }

    // ---------------------------------------------------------------
    // Input — three-finger hold, or Cast + any direction
    // ---------------------------------------------------------------

    private bool GetInputHeld()
    {
        // Fallback 1 — keyboard
        if (Input.GetKey(KeyCode.F)) return true;

        // Fallback 2 — gamepad
        if (Input.GetKey(KeyCode.JoystickButton2)) return true;

        // Primary A — Cast + direction (up / down / left / right)
        if (IsCastWithDirection()) return true;

        // Primary B — three-finger hold anywhere
        // Three fingers avoids conflicts with:
        //   - one thumb on the movement joystick + one thumb on an action button (2 fingers)
        //   - two-handed grip while attacking (2 fingers)
        int active = 0;
        for (int i = 0; i < Input.touchCount; i++)
        {
            var t = Input.GetTouch(i);
            if (t.phase == TouchPhase.Began ||
                t.phase == TouchPhase.Moved ||
                t.phase == TouchPhase.Stationary)
                active++;
        }
        return active >= 3;
    }

    private bool IsCastWithDirection()
    {
        try
        {
            var handler = ManagerSingleton<InputHandler>.Instance;
            if (handler == null) return false;

            var actions = handler.inputActions;
            if (actions == null) return false;

            bool cast = IsPressed(actions.Cast);
            if (!cast) return false;

            bool dir = IsPressed(actions.Up)
                    || IsPressed(actions.Down)
                    || IsPressed(actions.Left)
                    || IsPressed(actions.Right);

            return dir;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPressed(OneAxisInputControl control)
    {
        return control != null && control.IsPressed;
    }

    // ---------------------------------------------------------------
    // Audio lifecycle — charge sound only plays while charging
    // ---------------------------------------------------------------

    private void StartChargeAudio()
    {
        if (modAudioSource == null || clipCharge == null) return;
        if (modAudioSource.isPlaying) return;   // never restart an already-playing charge clip
        modAudioSource.clip = clipCharge;
        modAudioSource.loop = true;
        modAudioSource.Play();
    }

    private void EnsureAudioStopped()
    {
        if (modAudioSource == null) return;
        if (!modAudioSource.isPlaying) return;
        // Only stop the charge clip. Never touch the fire one-shot.
        if (modAudioSource.clip == clipCharge || clipCharge == null)
        {
            modAudioSource.Stop();
        }
    }

    // ---------------------------------------------------------------
    // Combat
    // ---------------------------------------------------------------

    private void FireNow()
    {
        fireQueued = false;
        hasFrozen = false;

        float charge = Mathf.Min(chargeTimer, MAX_CHARGE_TIME);
        float t = Mathf.Clamp01(charge / MAX_CHARGE_TIME);
        float mult = Mathf.Lerp(1f, MAX_DAMAGE_MULT, t);

        Logger.LogWarning($"[GOJO] FIRE! Held: {chargeTimer:F2}s | Mult: {mult:F1}x");

        var impact = hornetHero.transform.Find("Tool Effects/WebShot Effects/SnipeShot Impact");
        if (impact != null) ApplyDamageBoost(impact.gameObject, mult);

        var toolHornet = hornetHero.transform.Find("Tool Effects/Tool Hornet");
        if (toolHornet != null)
        {
            var anim = toolHornet.GetComponent<tk2dSpriteAnimator>();
            if (anim != null)
            {
                anim.Play(FIRE_ANIM);
                anim.ClipFps = 12f;
            }
        }

        ResetFPS();

        if (modAudioSource != null)
        {
            modAudioSource.Stop();
            if (clipFire != null) modAudioSource.PlayOneShot(clipFire);
        }
    }

    private void ApplyDamageBoost(GameObject obj, float multiplier)
    {
        foreach (var mb in obj.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;

            foreach (var f in mb.GetType().GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                string n = f.Name.ToLowerInvariant();
                if ((n.Contains("damage") || n.Contains("dmg") || n.Contains("power")) &&
                    f.FieldType == typeof(int))
                {
                    int cur = (int)f.GetValue(mb);
                    f.SetValue(mb, Mathf.RoundToInt(cur * multiplier));
                }
            }
        }
    }

    // ---------------------------------------------------------------
    // Material helpers
    // ---------------------------------------------------------------

    private void ApplyTex(string path, Texture2D tex)
    {
        if (tex == null) return;
        var tr = hornetHero.transform.Find(path);
        if (tr == null) return;

        var r = tr.GetComponent<Renderer>();
        if (r != null && r.material.mainTexture != tex)
            r.material.mainTexture = tex;
    }

    private void ApplyTint(string path)
    {
        var tr = hornetHero.transform.Find(path);
        if (tr == null) return;

        var r = tr.GetComponent<Renderer>();
        if (r == null) return;

        r.material.color = Color.red;
        if (r.material.HasProperty("_TintColor"))
            r.material.SetColor("_TintColor", Color.red);
    }

    private void CheckQueue()
    {
        var toolHornet = hornetHero.transform.Find("Tool Effects/Tool Hornet");
        if (toolHornet == null) return;

        var anim = toolHornet.GetComponent<tk2dSpriteAnimator>();
        if (anim != null && !anim.IsPlaying(ANTIC_ANIM) && !hasFrozen)
            FireNow();
    }

    private void ResetFPS()
    {
        foreach (var a in hornetHero.GetComponentsInChildren<tk2dSpriteAnimator>(true))
        {
            if (a.CurrentClip != null) a.ClipFps = a.CurrentClip.fps;
        }
    }

    // ---------------------------------------------------------------
    // Asset loading
    // ---------------------------------------------------------------

    private IEnumerator LoadTexture(string fileName, Action<Texture2D> onLoaded)
    {
        string folderPath = Path.Combine(Paths.PluginPath, "GojoSilkshot");
        string fullPath = Path.Combine(folderPath, fileName);

        if (!File.Exists(fullPath))
        {
            Logger.LogError("Texture not found: " + fullPath);
            yield break;
        }

        byte[] bytes = null;
        try { bytes = File.ReadAllBytes(fullPath); }
        catch (Exception ex) { Logger.LogError("Read failed: " + ex.Message); }

        if (bytes == null) yield break;

        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!tex.LoadImage(bytes))
        {
            Logger.LogError("LoadImage failed for " + fileName);
            yield break;
        }

        tex.name = fileName;
        tex.filterMode = FilterMode.Point;
        onLoaded?.Invoke(tex);
        yield return null;
    }

    private IEnumerator LoadAudio(string fileName, Action<AudioClip> onLoaded)
    {
        string folderPath = Path.Combine(Paths.PluginPath, "GojoSilkshot");
        string fullPath = Path.Combine(folderPath, fileName);

        if (!File.Exists(fullPath))
        {
            Logger.LogWarning("Audio not found: " + fullPath + " (silent mode)");
            yield break;
        }

        string url = "file://" + fullPath;
        UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV);
        try
        {
            yield return uwr.SendWebRequest();

            if (uwr.result != UnityWebRequest.Result.Success)
            {
                Logger.LogWarning("Audio load failed (" + fileName + "): " + uwr.error + " — silent mode");
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(uwr);
            clip.name = fileName;
            onLoaded?.Invoke(clip);
        }
        finally
        {
            uwr?.Dispose();
        }
    }
}
