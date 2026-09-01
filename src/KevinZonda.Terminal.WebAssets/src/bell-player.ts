export class BellPlayer {
  private static readonly MINIMUM_INTERVAL_MS = 100;
  private static readonly DURATION_SECONDS = 0.12;
  private static readonly START_FREQUENCY_HZ = 880;
  private static readonly END_FREQUENCY_HZ = 660;
  private static readonly PEAK_GAIN = 1.0;
  private static readonly SILENT_GAIN = 0.0001;

  private audioContext?: AudioContext;
  private lastPlayedAt = Number.NEGATIVE_INFINITY;

  public play(): void {
    const now = performance.now();
    if (now - this.lastPlayedAt < BellPlayer.MINIMUM_INTERVAL_MS) {
      return;
    }
    this.lastPlayedAt = now;

    const context = this.getAudioContext();
    if (!context) {
      return;
    }

    if (context.state === 'suspended') {
      void context.resume()
        .then(() => {
          if (context.state === 'running') {
            this.playTone(context);
          }
        })
        .catch(() => {
          // Browsers may reject playback until the page has received a user gesture.
        });
      return;
    }

    if (context.state === 'running') {
      this.playTone(context);
    }
  }

  private getAudioContext(): AudioContext | undefined {
    if (this.audioContext) {
      return this.audioContext;
    }

    try {
      this.audioContext = new AudioContext();
      return this.audioContext;
    } catch {
      // Audio playback is best-effort and must not interrupt terminal output.
      return undefined;
    }
  }

  private playTone(context: AudioContext): void {
    const startedAt = context.currentTime;
    const stoppedAt = startedAt + BellPlayer.DURATION_SECONDS;
    const oscillator = context.createOscillator();
    const gain = context.createGain();

    oscillator.type = 'sine';
    oscillator.frequency.setValueAtTime(BellPlayer.START_FREQUENCY_HZ, startedAt);
    oscillator.frequency.exponentialRampToValueAtTime(
      BellPlayer.END_FREQUENCY_HZ,
      stoppedAt);

    gain.gain.setValueAtTime(BellPlayer.PEAK_GAIN, startedAt);
    gain.gain.exponentialRampToValueAtTime(BellPlayer.SILENT_GAIN, stoppedAt);

    oscillator.connect(gain);
    gain.connect(context.destination);
    oscillator.addEventListener('ended', () => {
      oscillator.disconnect();
      gain.disconnect();
    }, { once: true });
    oscillator.start(startedAt);
    oscillator.stop(stoppedAt);
  }
}
