using UnityEngine;

/// <summary>
/// All sound is synthesized at runtime with AudioClip.Create — no audio files.
/// Ping = soft sine sweep down (with random pitch), wall tick = tiny blip (pitch rises as the
/// ring sweeps), level clear = a rising arpeggio whose length reflects the grade,
/// plus hull lost/repaired, rewind, heartbeat and UI cues.
///
/// Since the audio pass, sound also carries INFORMATION rather than only reacting to it:
///   * echoes are panned toward the side of the maze that answered (SonarManager works out where)
///   * the exit gives off a beacon that speeds up and brightens as you close in
///   * a decoy hums while it is solid, so you can hear when it is safe to cross
///   * a hunter growls as it closes, panned to where it is
///   * under five seconds the countdown becomes a heartbeat
/// For a game called Sonarfall, sound was the biggest gap; most of the levels can now be read by
/// ear as well as by eye.
///
/// Music (the ambient bed) and effects mute independently. Pings carry information, and a lot of
/// players want their own music on while they still hear them.
/// </summary>
public class ProceduralAudio : MonoBehaviour
{
    private const int SampleRate = 44100;
    /// <summary>The ambient bed is all slow, mostly low partials — half the rate is half the memory
    /// for a sixteen-second loop and costs nothing audible.</summary>
    private const int BedRate = 22050;
    private const float BedSeconds = 16f;
    private const float BedVolume = 0.34f;

    private AudioSource _main;   // ping / clear / hull / rewind / heartbeat
    private AudioSource _move;   // looping movement whoosh (volume rides speed)
    private AudioSource _ui;     // own source so UI never cuts off a gameplay cue mid-play
    private AudioSource _bed;    // MUSIC: the only source the music switch touches
    private AudioSource _beacon; // exit pips — own source so its pan never swings another cue
    private AudioSource _gate;   // gate slams, panned to the gate
    private AudioSource[] _ticks;   // echo ticks, round-robin so each keeps its own pan
    private int _nextTick;

    private AudioClip _ping, _tickClip, _star, _moveClip, _rewind, _lose, _timeWarn;
    private AudioClip _hullLost, _hullRepaired, _whoosh, _button, _teach, _exitHit, _wheelTick;
    private AudioClip _grade1, _grade2, _grade3, _grade3Sector, _reinforced;
    private AudioClip _heart, _beaconClip, _gateClip, _riser;
    private float _moveTargetVol, _moveLevel;
    private float _bedTarget = BedVolume;

    /// <summary>A looping voice whose volume and pan are driven every frame by gameplay.</summary>
    private sealed class Loop
    {
        public AudioSource src;
        public float target, pan;
    }

    private Loop[] _hums;           // one per decoy slot
    private Loop _hunter, _current;

    public void Init()
    {
        _main = NewSource(1f);
        _move = NewSource(0f); _move.loop = true;
        _ui = NewSource(0.5f);
        _beacon = NewSource(1f);
        _gate = NewSource(1f);
        _ticks = new AudioSource[4];
        for (int i = 0; i < _ticks.Length; i++) _ticks[i] = NewSource(0.28f);

        _ping     = BuildSweep(880f, 220f, 0.35f, 0.5f);
        _tickClip = BuildSweep(1400f, 1100f, 0.035f, 0.6f);
        _star     = BuildSweep(1200f, 1600f, 0.09f, 0.45f);
        _rewind   = BuildRewind();
        _lose     = BuildArpeggio(new[] { 392f, 311f, 233f }, 0.18f, 0.5f); // descending = "aww, failed"
        _timeWarn = BuildSweep(620f, 620f, 0.18f, 0.45f);                   // steady heads-up beep
        _moveClip = BuildMoveLoop();

        // Losing a plate is the harshest thing that happens in a level, so it gets the harshest
        // sound: three notes falling more than an octave.
        _hullLost     = BuildArpeggio(new[] { 523.25f, 349.23f, 220f }, 0.10f, 0.55f);
        // Repairing one mirrors it exactly — same three notes, climbing.
        _hullRepaired = BuildArpeggio(new[] { 523.25f, 783.99f, 1046.5f }, 0.09f, 0.5f);
        _whoosh     = BuildSweep(300f, 900f, 0.22f, 0.35f);   // level regenerated
        _button     = BuildSweep(1500f, 1900f, 0.035f, 0.30f); // dry UI click
        _teach      = BuildArpeggio(new[] { 659.25f, 880f }, 0.07f, 0.35f); // callout appears
        // Level-wheel detent. Shorter and softer than the button click so a fast flick through
        // thirty levels is a texture rather than thirty separate clicks, and pitched a little above
        // it so the two never read as the same event. Falling, so each notch sounds like it lands.
        _wheelTick  = BuildSweep(2100f, 1650f, 0.022f, 0.22f);

        // Reaching the exit: a C4 bloom that bends into tune. Deliberately still ringing when the
        // grade sting lands ~0.4s later — it is the bass the sting resolves over, not a separate
        // announcement competing with it.
        _exitHit = BuildArrivalBloom(261.63f, 1.5f, 0.6f);

        // Level-clear stings, one per grade. All resolve upward on the major triad so a clear
        // always feels like an arrival — they differ in how far they climb, so three stars is
        // audibly a bigger deal without being a different piece of music.
        _grade1 = BuildArpeggio(new[] { 523.25f, 659.25f, 783.99f }, 0.10f, 0.5f);
        _grade2 = BuildArpeggio(new[] { 523.25f, 659.25f, 783.99f, 1046.50f }, 0.09f, 0.55f);
        _grade3 = BuildArpeggio(new[] { 523.25f, 659.25f, 783.99f, 1046.50f, 1318.51f, 1567.98f }, 0.075f, 0.6f);
        // Three stars on a sector finale: the same triad an octave up, running all the way to C7.
        _grade3Sector = BuildArpeggio(new[] { 523.25f, 783.99f, 1046.50f, 1318.51f, 1567.98f, 2093.00f, 2637.02f },
                                      0.068f, 0.62f);

        // The gold fourth plate. A fast bright rise so it lands as a windfall.
        _reinforced = BuildArpeggio(new[] { 783.99f, 1046.50f, 1318.51f, 1567.98f }, 0.065f, 0.6f);

        _heart      = BuildHeartbeat();
        _beaconClip = BuildBeacon();
        _gateClip   = BuildGateSlam();
        _riser      = BuildRiser();

        // Looping voices. They are stopped outright while silent rather than left running at zero:
        // eight idle loops still cost a mixer voice each, every frame, on a phone.
        _hums = new Loop[GameConfig.MaxDecoys];
        var hum = BuildHum();
        for (int i = 0; i < _hums.Length; i++)
        {
            _hums[i] = NewLoop(hum);
            _hums[i].src.pitch = 0.92f + 0.05f * i;   // each lure its own note, so two read as two
        }
        _hunter  = NewLoop(BuildGrowl());
        _current = NewLoop(BuildRush());

        _move.clip = _moveClip;
        _move.Play(); // runs continuously at volume 0; SetMoveLevel opens it up

        // The bed: the trailer's mood, in the game. Starts on the menu and never stops.
        _bed = NewSource(0f);
        _bed.loop = true;
        _bed.clip = BuildBed();
        _bed.Play();

        ApplyMix();
    }

    private AudioSource NewSource(float volume)
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false; s.spatialBlend = 0f; s.volume = volume;
        return s;
    }

    private Loop NewLoop(AudioClip clip)
    {
        var s = NewSource(0f);
        s.loop = true;
        s.clip = clip;
        return new Loop { src = s };
    }

    /// <summary>
    /// Push the MUSIC and EFFECTS switches onto the sources. Mute, not volume, so the per-frame
    /// volume rides (movement, hums, ducking) keep running underneath and come back exactly where
    /// they would have been.
    /// </summary>
    public void ApplyMix()
    {
        bool music = SaveData.MusicOn, sfx = SaveData.SfxOn;
        var all = GetComponents<AudioSource>();
        for (int i = 0; i < all.Length; i++)
            all[i].mute = all[i] == _bed ? !music : !sfx;
    }

    // ---------- one-shots ----------

    /// <summary>Time-rewind shimmer for the decoy penalty (kept gentle).</summary>
    public void PlayRewind() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_rewind, 0.6f); } }

    /// <summary>The moment of touching the exit — a C-major bloom that becomes the bass bed for
    /// the grade sting arriving on top of it.</summary>
    public void PlayExitReached() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_exitHit, 0.85f); } }

    /// <summary>The level-clear sting. <paramref name="grade"/> (1..3) picks how far it climbs.</summary>
    public void PlayGrade(int grade, bool sectorFinale)
    {
        if (!_main) return;
        _main.pitch = 1f;
        AudioClip c = grade >= 3 ? (sectorFinale ? _grade3Sector : _grade3)
                    : grade == 2 ? _grade2
                                 : _grade1;
        _main.PlayOneShot(c, grade >= 3 ? 1f : 0.9f);
    }

    /// <summary>The fourth, gold plate, taken at full hull. The brightest cue in the game.</summary>
    public void PlayReinforced() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_reinforced, 0.95f); } }

    /// <summary>A plate cracked. Deliberately the loudest negative cue in the game.</summary>
    public void PlayHullLost() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_hullLost, 0.9f); } }

    /// <summary>A plate repaired — the hull-lost motif played backwards.</summary>
    public void PlayHullRepaired() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_hullRepaired, 0.85f); } }

    /// <summary>Level regenerated (reset button).</summary>
    public void PlayWhoosh() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_whoosh, 0.5f); } }

    /// <summary>The slow rising sweep from the trailer: a new sector, or the start of a dive.</summary>
    public void PlayRiser() { if (_ui && _riser) { _ui.pitch = 1f; _ui.PlayOneShot(_riser, 0.7f); } }

    /// <summary>Every UI button. On its own source so it can never cut a gameplay cue short.</summary>
    // Clip guarded as well as the source: this fires from menu chrome that can be driven before
    // the clips finish building, and PlayOneShot(null) logs a warning every single press.
    public void PlayButton() { if (_ui && _button) { _ui.pitch = 1f; _ui.PlayOneShot(_button, 0.6f); } }

    /// <summary>A callout appearing.</summary>
    public void PlayTeach() { if (_ui && _teach) { _ui.pitch = 1f; _ui.PlayOneShot(_teach, 0.7f); } }

    /// <summary>
    /// One detent of the level wheel. <paramref name="climb"/> nudges the pitch up as the number
    /// rises and down as it falls, so scrolling has a direction you can hear — a flat repeated
    /// blip is what makes a picker feel mechanical instead of tactile.
    /// </summary>
    public void PlayWheelTick(float climb)
    {
        if (!_ui || !_wheelTick) return;
        _ui.pitch = Mathf.Clamp(1f + climb * 0.06f, 0.82f, 1.22f);
        _ui.PlayOneShot(_wheelTick, 0.55f);
    }

    public void PlayLose() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_lose, 0.8f); } }
    public void PlayTimeWarning() { if (_main) { _main.pitch = 1f; _main.PlayOneShot(_timeWarn, 0.5f); } }

    /// <summary>
    /// The last five seconds. A heartbeat rather than the old metronome tick: a tick says "time
    /// is passing", a pulse says "you are in trouble", and it is the trailer's sound. It quickens
    /// and swells as the count falls.
    /// </summary>
    public void PlayHeartbeat(int secsLeft)
    {
        if (_main == null) return;
        int k = 5 - Mathf.Clamp(secsLeft, 1, 5);           // 0 at 5s .. 4 at 1s
        _main.pitch = 1f + k * 0.05f;
        _main.PlayOneShot(_heart, 0.75f + k * 0.06f);
    }

    /// <summary>Stars popping onto the clear screen, climbing with each one.</summary>
    public void PlayStar(int index) { if (_main) { _main.pitch = 1f + index * 0.18f; _main.PlayOneShot(_star); } }

    /// <summary>
    /// One pip of the exit beacon. Pan says which way, volume and pitch say how near —
    /// GameManager chooses all three, and how often to call this.
    /// </summary>
    public void PlayBeacon(float pan, float volume, float pitch)
    {
        if (!_beacon) return;
        _beacon.panStereo = Mathf.Clamp(pan, -1f, 1f);
        _beacon.pitch = pitch;
        _beacon.PlayOneShot(_beaconClip, volume);
    }

    /// <summary>A gate slamming shut somewhere — panned and attenuated by the caller.</summary>
    public void PlayGate(float pan, float volume)
    {
        if (!_gate || volume <= 0.01f) return;
        _gate.panStereo = Mathf.Clamp(pan, -1f, 1f);
        _gate.pitch = Random.Range(0.94f, 1.06f);
        _gate.PlayOneShot(_gateClip, volume);
    }

    public void PlayPing(float pitch = 1f) { if (_main) { _main.pitch = pitch; _main.PlayOneShot(_ping); } }

    /// <summary>An echo coming back. <paramref name="pan"/> is where the answering walls are.</summary>
    public void PlayTick(float pitch = 1f, float pan = 0f)
    {
        if (_ticks == null) return;
        var s = _ticks[_nextTick];
        _nextTick = (_nextTick + 1) % _ticks.Length;
        s.pitch = Mathf.Clamp(pitch, 0.6f, 2.5f);
        s.panStereo = pan;
        s.PlayOneShot(_tickClip);
    }

    // ---------- driven loops ----------

    /// <summary>0 = still (silent), 1 = full speed. Called by the player each frame.</summary>
    public void SetMoveLevel(float level)
    {
        _moveLevel = Mathf.Clamp01(level);
        _moveTargetVol = _moveLevel * GameConfig.MoveAudioMaxVol;
    }

    /// <summary>Decoy <paramref name="i"/>'s hum: loud while it is solid and near, silent while dark.</summary>
    public void SetHum(int i, float volume, float pan)
    {
        if (_hums == null || i < 0 || i >= _hums.Length) return;
        _hums[i].target = volume; _hums[i].pan = pan;
    }

    public void SetHunter(float volume, float pan) { if (_hunter != null) { _hunter.target = volume; _hunter.pan = pan; } }
    public void SetCurrent(float volume, float pan) { if (_current != null) { _current.target = volume; _current.pan = pan; } }

    /// <summary>
    /// 0 = calm, 1 = the last seconds. The bed ducks under the heartbeat rather than fighting it.
    /// </summary>
    public void SetTension(float t) => _bedTarget = Mathf.Lerp(BedVolume, BedVolume * 0.4f, Mathf.Clamp01(t));

    /// <summary>Kill every gameplay loop at once — leaving a level, failing it, the menu.</summary>
    public void SilenceWorld()
    {
        if (_hums != null) for (int i = 0; i < _hums.Length; i++) _hums[i].target = 0f;
        if (_hunter != null) _hunter.target = 0f;
        if (_current != null) _current.target = 0f;
        _bedTarget = BedVolume;
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        if (_bed != null && _bed.volume != _bedTarget)
            _bed.volume = Mathf.MoveTowards(_bed.volume, _bedTarget, dt * 0.25f);

        if (_hums != null) for (int i = 0; i < _hums.Length; i++) TickLoop(_hums[i], dt);
        TickLoop(_hunter, dt);
        TickLoop(_current, dt);

        if (_move == null) return;
        // Idle most of the time (menus, a dot at rest): nothing to ramp, so skip the two native
        // property writes rather than re-asserting silence every frame.
        if (_moveTargetVol <= 0f && _move.volume <= 0f) return;
        // Smoothly open/close the whoosh and bend its pitch up with speed.
        _move.volume = Mathf.MoveTowards(_move.volume, _moveTargetVol, dt * 0.8f);
        float targetPitch = 0.8f + 0.5f * _moveLevel;
        _move.pitch = Mathf.Lerp(_move.pitch, targetPitch, dt * 6f);
    }

    private static void TickLoop(Loop l, float dt)
    {
        if (l == null) return;
        var s = l.src;
        if (l.target <= 0f && s.volume <= 0f)
        {
            if (s.isPlaying) s.Stop();
            return;
        }
        if (!s.isPlaying) { s.volume = 0f; s.Play(); }
        s.volume = Mathf.MoveTowards(s.volume, l.target, dt * 1.6f);
        s.panStereo = Mathf.Lerp(s.panStereo, l.pan, Mathf.Min(1f, dt * 8f));
    }

    // ---------- synthesis ----------

    private AudioClip BuildSweep(float startHz, float endHz, float duration, float volume)
    {
        int samples = Mathf.Max(1, Mathf.RoundToInt(SampleRate * duration));
        var data = new float[samples];
        double phase = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;
            float freq = Mathf.Lerp(startHz, endHz, t);
            phase += 2.0 * Mathf.PI * freq / SampleRate;
            float attack = Mathf.Clamp01(t / 0.05f);
            float decay = Mathf.Exp(-3f * t);
            data[i] = Mathf.Sin((float)phase) * attack * decay * volume;
        }
        var clip = AudioClip.Create("sweep", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// Contact. A warm C-major bloom of pure sines that bends UP into tune and rings out.
    ///
    /// This is the bass note the grade sting resolves over, not a rival to it. The sting is a
    /// C-major arpeggio from C5; this is rooted an octave below with its own octave partial
    /// landing on that same C5. They are one chord voiced across two events, which is why the
    /// bloom can still be ringing when the sting arrives without the two colliding — bass under
    /// melody is how music has always worked.
    ///
    /// The pitch bend is the hook. It starts 2.8% flat and settles over 130ms, so the sound
    /// audibly locks into place. That resolution is the satisfying part, and it mirrors what the
    /// player just did: the sonar has been searching in the dark, and this is contact landing.
    ///
    /// Rooted at C4 rather than lower on purpose: phone speakers roll off below roughly 300Hz,
    /// so a genuinely deep bloom would simply not exist on the device this ships to.
    /// </summary>
    private AudioClip BuildArrivalBloom(float rootHz, float duration, float volume)
    {
        int samples = Mathf.Max(1, Mathf.RoundToInt(SampleRate * duration));
        var data = new float[samples];

        float[] ratios = { 1f, 1.5f, 2f, 3f };        // root, fifth, octave, twelfth
        float[] gains  = { 1f, 0.42f, 0.55f, 0.14f };
        const float gainSum = 2.11f;
        var phases = new double[ratios.Length];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;
            float secs = i / (float)SampleRate;

            // Bend up into tune over the first 130ms — the "locking on" that makes it land.
            float bend = Mathf.Lerp(0.972f, 1f, Easing.OutCubic(Mathf.Clamp01(secs / 0.13f)));

            float sample = 0f;
            for (int p = 0; p < ratios.Length; p++)
            {
                phases[p] += 2.0 * Mathf.PI * (rootHz * ratios[p] * bend) / SampleRate;
                sample += Mathf.Sin((float)phases[p]) * gains[p];
            }
            sample /= gainSum;

            // Soft swell in (no click), long ring out, breathing slowly as it goes.
            float attack   = Mathf.Clamp01(secs / 0.028f);
            float body     = Mathf.Exp(-2.6f * t);
            float tremolo  = 1f + 0.08f * Mathf.Sin(2f * Mathf.PI * 5.5f * secs) * (1f - t);

            data[i] = sample * attack * body * tremolo * volume;
        }

        var clip = AudioClip.Create("arrivalBloom", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private AudioClip BuildArpeggio(float[] notesHz, float noteDuration, float volume)
    {
        int noteSamples = Mathf.RoundToInt(SampleRate * noteDuration);
        int total = noteSamples * notesHz.Length;
        var data = new float[total];
        for (int n = 0; n < notesHz.Length; n++)
        {
            double phase = 0.0;
            float freq = notesHz[n];
            for (int i = 0; i < noteSamples; i++)
            {
                float t = (float)i / noteSamples;
                phase += 2.0 * Mathf.PI * freq / SampleRate;
                float attack = Mathf.Clamp01(t / 0.04f);
                float decay = Mathf.Exp(-2.5f * t);
                data[n * noteSamples + i] = Mathf.Sin((float)phase) * attack * decay * volume;
            }
        }
        var clip = AudioClip.Create("arp", total, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// The ambient bed — the trailer's low drone, in the game. An E-minor pad in two layers:
    /// E2/B2/E3 for headphones, and E4/G4/B4 because a phone speaker reproduces next to nothing
    /// under ~300Hz and a bed made only of the low layer would be silence on the device it ships
    /// to. Each partial breathes on its own slow LFO so the chord shifts colour without ever
    /// changing.
    ///
    /// Seamless by construction: every frequency (partials and LFOs alike) is rounded to a whole
    /// number of cycles in BedSeconds, so the last sample flows straight into the first. Detuning
    /// from that rounding is under 0.07Hz — inaudible.
    /// </summary>
    private AudioClip BuildBed()
    {
        int n = Mathf.RoundToInt(BedRate * BedSeconds);
        var data = new float[n];
        const float TAU = 2f * Mathf.PI;

        float[] hz   = { 82.41f, 123.47f, 164.81f, 329.63f, 392.00f, 493.88f, 1318.5f };
        float[] gain = { 0.55f,  0.32f,   0.30f,   0.20f,   0.13f,   0.15f,   0.025f };
        float[] lfo  = { 0.0625f, 0.125f, 0.1875f, 0.125f,  0.0625f, 0.1875f, 0.25f };
        for (int p = 0; p < hz.Length; p++) hz[p] = Mathf.Round(hz[p] * BedSeconds) / BedSeconds;

        float norm = 0f;
        for (int p = 0; p < gain.Length; p++) norm += gain[p];

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)BedRate;
            float s = 0f;
            for (int p = 0; p < hz.Length; p++)
            {
                float breathe = 0.55f + 0.45f * Mathf.Sin(TAU * lfo[p] * t + p * 1.7f);
                s += Mathf.Sin(TAU * hz[p] * t) * gain[p] * breathe;
            }
            data[i] = s / norm;
        }

        var clip = AudioClip.Create("bed", n, 1, BedRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// Lub-dub. The kick from the trailer — a fast downward bend into a low body — plus its 2nd
    /// and 3rd harmonics, because the 46Hz fundamental alone does not exist on a phone speaker.
    /// </summary>
    private AudioClip BuildHeartbeat()
    {
        float dur = 0.6f;
        int samples = Mathf.RoundToInt(SampleRate * dur);
        var data = new float[samples];
        AddThump(data, 0f, 1f, 1f);
        AddThump(data, 0.21f, 0.62f, 1.12f);
        var clip = AudioClip.Create("heart", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static void AddThump(float[] data, float at, float amp, float pitch)
    {
        int start = Mathf.RoundToInt(at * SampleRate);
        double phase = 0.0;
        for (int i = start; i < data.Length; i++)
        {
            float t = (i - start) / (float)SampleRate;
            float f = (120f * Mathf.Exp(-t * 28f) + 46f) * pitch;
            phase += 2.0 * Mathf.PI * f / SampleRate;
            float env = Mathf.Exp(-t * 11f) * Mathf.Clamp01(t / 0.003f);
            float s = Mathf.Sin((float)phase) + 0.55f * Mathf.Sin((float)(phase * 2.0)) + 0.25f * Mathf.Sin((float)(phase * 3.0));
            data[i] += s / 1.8f * env * amp * 0.9f;
        }
    }

    /// <summary>The exit's pip: a clean high sine that bends down a touch, like a sonar return.</summary>
    private AudioClip BuildBeacon()
    {
        float dur = 0.34f;
        int samples = Mathf.RoundToInt(SampleRate * dur);
        var data = new float[samples];
        double phase = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)SampleRate;
            float f = Mathf.Lerp(1174.66f, 1108.73f, t / dur);
            phase += 2.0 * Mathf.PI * f / SampleRate;
            float env = Mathf.Clamp01(t / 0.006f) * Mathf.Exp(-t * 11f);
            data[i] = (Mathf.Sin((float)phase) + 0.18f * Mathf.Sin((float)(phase * 1.5))) * env * 0.4f;
        }
        var clip = AudioClip.Create("beacon", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>A gate shutting: a low thud with a short metallic ring on top.</summary>
    private AudioClip BuildGateSlam()
    {
        float dur = 0.22f;
        int samples = Mathf.RoundToInt(SampleRate * dur);
        var data = new float[samples];
        double p1 = 0.0, p2 = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)SampleRate;
            p1 += 2.0 * Mathf.PI * Mathf.Lerp(220f, 110f, t / dur) / SampleRate;
            p2 += 2.0 * Mathf.PI * 1244.5f / SampleRate;
            float thud = Mathf.Sin((float)p1) * Mathf.Exp(-t * 18f);
            float ring = Mathf.Sin((float)p2) * Mathf.Exp(-t * 40f) * 0.35f;
            data[i] = (thud + ring) * Mathf.Clamp01(t / 0.002f) * 0.55f;
        }
        var clip = AudioClip.Create("gate", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>The trailer's rising sweep, short: swells in rather than starting on a hit.</summary>
    private AudioClip BuildRiser()
    {
        float dur = 1.4f;
        int samples = Mathf.RoundToInt(SampleRate * dur);
        var data = new float[samples];
        double p1 = 0.0, p2 = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)samples;
            float f = Mathf.Lerp(180f, 720f, t * t);
            p1 += 2.0 * Mathf.PI * f / SampleRate;
            p2 += 2.0 * Mathf.PI * f * 1.5 / SampleRate;
            float env = Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.7f));
            data[i] = (Mathf.Sin((float)p1) + 0.4f * Mathf.Sin((float)p2)) * env * 0.22f;
        }
        var clip = AudioClip.Create("riser", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// A decoy's hum: a low, slightly beating triad with a 4Hz wobble — a live thing, not a tone.
    /// One-second seamless loop (whole-Hz partials, whole-Hz wobble).
    /// </summary>
    private AudioClip BuildHum()
    {
        int n = SampleRate;
        var data = new float[n];
        const float TAU = 2f * Mathf.PI;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float s = Mathf.Sin(TAU * 220f * t) * 0.5f + Mathf.Sin(TAU * 221f * t) * 0.35f
                    + Mathf.Sin(TAU * 330f * t) * 0.25f + Mathf.Sin(TAU * 440f * t) * 0.12f;
            float wob = 0.72f + 0.28f * Mathf.Sin(TAU * 4f * t);
            data[i] = s * wob * 0.32f;
        }
        var clip = AudioClip.Create("hum", n, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>The hunter: a low pulsing growl at 7 beats a second. Seamless one-second loop.</summary>
    private AudioClip BuildGrowl()
    {
        int n = SampleRate;
        var data = new float[n];
        const float TAU = 2f * Mathf.PI;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float s = Mathf.Sin(TAU * 124f * t) * 0.6f + Mathf.Sin(TAU * 186f * t) * 0.35f
                    + Mathf.Sin(TAU * 62f * t) * 0.5f + Mathf.Sin(TAU * 311f * t) * 0.12f;
            float pulse = Mathf.Pow(0.5f + 0.5f * Mathf.Sin(TAU * 7f * t), 2f);
            data[i] = s * (0.25f + 0.75f * pulse) * 0.3f;
        }
        var clip = AudioClip.Create("growl", n, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// A current's rush. A cloud of unrelated sines reads as moving water where one sine never
    /// could; every partial is a whole Hz so the second loops without a seam, and the seed is fixed
    /// so the texture is identical every launch.
    /// </summary>
    private AudioClip BuildRush()
    {
        int n = SampleRate;
        var data = new float[n];
        const float TAU = 2f * Mathf.PI;
        var rng = new System.Random(4242);
        const int partials = 26;
        var hz = new float[partials];
        var ph = new float[partials];
        var amp = new float[partials];
        for (int p = 0; p < partials; p++)
        {
            hz[p] = rng.Next(260, 1400);
            ph[p] = (float)rng.NextDouble() * TAU;
            amp[p] = 0.4f + 0.6f * (float)rng.NextDouble();
        }
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float s = 0f;
            for (int p = 0; p < partials; p++) s += Mathf.Sin(TAU * hz[p] * t + ph[p]) * amp[p];
            float swell = 0.7f + 0.3f * Mathf.Sin(TAU * 2f * t);
            data[i] = s / partials * swell * 0.5f;
        }
        var clip = AudioClip.Create("rush", n, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// Seamless 1-second movement drone: a warm synth pad from detuned sine oscillators + gentle
    /// harmonics, with a slow tremolo. All frequencies are whole Hz so each completes an exact
    /// number of cycles in 1 second -> the loop is perfectly seamless (no noise, no cloth-rustle,
    /// no click). Pitch bends up with speed at playback time.
    /// </summary>
    private AudioClip BuildMoveLoop()
    {
        int n = SampleRate;
        var data = new float[n];
        const float TAU = 2f * Mathf.PI;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            // Two slightly detuned low sines beat together for a living "engine" body,
            // plus a soft octave and a fifth for a sci-fi shimmer.
            float body =
                  Mathf.Sin(TAU * 100f * t) * 0.5f
                + Mathf.Sin(TAU * 101f * t) * 0.5f   // 1 Hz beat
                + Mathf.Sin(TAU * 200f * t) * 0.14f  // octave
                + Mathf.Sin(TAU * 150f * t) * 0.10f; // fifth
            float tremolo = 0.82f + 0.18f * Mathf.Sin(TAU * 3f * t); // slow amplitude LFO
            data[i] = body * tremolo * 0.5f;
        }

        var clip = AudioClip.Create("move", n, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// Soft, dreamy time-rewind shimmer: two pure sines gliding downward together with a
    /// gentle sine tremolo (no square-wave stutter) and a smooth bell envelope. Quiet and
    /// non-harsh, so it reads as "rewinding" without being abrasive.
    /// </summary>
    private AudioClip BuildRewind()
    {
        float duration = 0.9f;
        int samples = Mathf.RoundToInt(SampleRate * duration);
        var data = new float[samples];
        double p1 = 0.0, p2 = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;
            float freq = Mathf.Lerp(760f, 260f, Easing.OutQuad(t));      // smooth glide down
            p1 += 2.0 * Mathf.PI * freq / SampleRate;
            p2 += 2.0 * Mathf.PI * (freq * 1.5f) / SampleRate;           // a soft fifth on top
            float tremolo = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 9f * t); // gentle wobble
            float env = Mathf.Sin(Mathf.PI * t);                         // smooth fade in AND out (bell)
            float s = Mathf.Sin((float)p1) + 0.35f * Mathf.Sin((float)p2);
            data[i] = s * tremolo * env * 0.16f;                         // low amplitude
        }
        var clip = AudioClip.Create("rewind", samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
