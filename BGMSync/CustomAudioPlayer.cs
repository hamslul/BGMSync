using NAudio.Wave;
using System;

namespace BGMSync;

/// <summary>
/// Plays a custom audio file from disk, looping seamlessly via LoopStream.
/// Supports volume control (0.0 – 1.0).
/// </summary>
internal sealed class CustomAudioPlayer : IDisposable
{
    private WaveOutEvent?    _output;
    private LoopStream?      _loop;
    private AudioFileReader? _reader;
    private float            _volume = 1f;

    public bool    IsPlaying   => _output?.PlaybackState == PlaybackState.Playing;
    public string? CurrentPath { get; private set; }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_output != null) _output.Volume = _volume;
        }
    }

    public void Play(string path)
    {
        if (CurrentPath == path && IsPlaying) return;
        Stop();
        try
        {
            _reader = new AudioFileReader(path);
            _loop   = new LoopStream(_reader);
            _output = new WaveOutEvent { Volume = _volume };
            _output.Init(_loop);
            CurrentPath = path;
            _output.Play();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[BGMSync] Failed to play custom audio: {path}");
            DisposeResources();
        }
    }

    public void Stop()
    {
        DisposeResources();
        CurrentPath = null;
    }

    private void DisposeResources()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _loop?.Dispose();
        _loop = null;
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Wraps a WaveStream and loops it seamlessly by resetting position at EOF.
/// </summary>
internal sealed class LoopStream : WaveStream
{
    private readonly WaveStream _source;

    public LoopStream(WaveStream source) => _source = source;

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long       Length     => _source.Length;
    public override long       Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = _source.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                if (_source.Position == 0) break; // empty source — prevent infinite loop
                _source.Position = 0;
            }
            totalRead += read;
        }
        return totalRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}
