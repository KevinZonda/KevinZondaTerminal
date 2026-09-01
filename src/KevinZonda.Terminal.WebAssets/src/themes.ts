import type { ITheme } from '@xterm/xterm';

export const DEFAULT_THEME_NAME = 'KevinZonda Terminal Dark';

interface TerminalThemePreset {
  name: string;
  theme: ITheme;
}

const KEVINZONDA_TERMINAL_DARK_THEME: TerminalThemePreset = {
  name: DEFAULT_THEME_NAME,
  theme: {
    background: '#0c0f14', foreground: '#d8dee9', cursor: '#8fbcbb',
    cursorAccent: '#0c0f14', selectionBackground: '#3b5268',
    black: '#1b2028', red: '#e06c75', green: '#98c379', yellow: '#e5c07b',
    blue: '#61afef', magenta: '#c678dd', cyan: '#56b6c2', white: '#abb2bf',
    brightBlack: '#5c6370', brightRed: '#e06c75', brightGreen: '#98c379',
    brightYellow: '#e5c07b', brightBlue: '#61afef', brightMagenta: '#c678dd',
    brightCyan: '#56b6c2', brightWhite: '#ffffff'
  }
};

const TERMINAL_THEMES: TerminalThemePreset[] = [
  KEVINZONDA_TERMINAL_DARK_THEME,
  {
    name: 'Pro',
    theme: {
      background: '#000000', foreground: '#f2f2f2', cursor: '#4d4d4d',
      cursorAccent: '#000000', selectionBackground: '#414141',
      black: '#000000', red: '#990000', green: '#00a600', yellow: '#999900',
      blue: '#2009db', magenta: '#b200b2', cyan: '#00a6b2', white: '#bfbfbf',
      brightBlack: '#666666', brightRed: '#e50000', brightGreen: '#00d900',
      brightYellow: '#e5e500', brightBlue: '#0000ff', brightMagenta: '#e500e5',
      brightCyan: '#00e5e5', brightWhite: '#e5e5e5'
    }
  },
  {
    name: 'Ubuntu',
    theme: {
      background: '#300a24', foreground: '#eeeeec', cursor: '#bbbbbb',
      cursorAccent: '#300a24', selectionBackground: '#b5d5ff',
      black: '#2e3436', red: '#cc0000', green: '#4e9a06', yellow: '#c4a000',
      blue: '#3465a4', magenta: '#75507b', cyan: '#06989a', white: '#d3d7cf',
      brightBlack: '#555753', brightRed: '#ef2929', brightGreen: '#8ae234',
      brightYellow: '#fce94f', brightBlue: '#729fcf', brightMagenta: '#ad7fa8',
      brightCyan: '#34e2e2', brightWhite: '#eeeeec'
    }
  },
  {
    name: 'Campbell Powershell',
    theme: {
      background: '#012456', foreground: '#CCCCCC', cursor: '#FFFFFF',
      cursorAccent: '#012456', selectionBackground: '#3b5268',
      black: '#0C0C0C', red: '#C50F1F', green: '#13A10E', yellow: '#C19C00',
      blue: '#0037DA', magenta: '#881798', cyan: '#3A96DD', white: '#CCCCCC',
      brightBlack: '#767676', brightRed: '#E74856', brightGreen: '#16C60C',
      brightYellow: '#F9F1A5', brightBlue: '#3B78FF', brightMagenta: '#B4009E',
      brightCyan: '#61D6D6', brightWhite: '#F2F2F2'
    }
  },
  {
    name: 'Builtin Tango Dark',
    theme: {
      background: '#000000', foreground: '#ffffff', cursor: '#ffffff',
      cursorAccent: '#000000', selectionBackground: '#b5d5ff',
      black: '#000000', red: '#cc0000', green: '#4e9a06', yellow: '#c4a000',
      blue: '#3465a4', magenta: '#75507b', cyan: '#06989a', white: '#d3d7cf',
      brightBlack: '#555753', brightRed: '#ef2929', brightGreen: '#8ae234',
      brightYellow: '#fce94f', brightBlue: '#729fcf', brightMagenta: '#ad7fa8',
      brightCyan: '#34e2e2', brightWhite: '#eeeeec'
    }
  },
  {
    name: 'Campbell',
    theme: {
      background: '#0C0C0C', foreground: '#CCCCCC', cursor: '#FFFFFF',
      cursorAccent: '#0C0C0C', selectionBackground: '#3b5268',
      black: '#0C0C0C', red: '#C50F1F', green: '#13A10E', yellow: '#C19C00',
      blue: '#0037DA', magenta: '#881798', cyan: '#3A96DD', white: '#CCCCCC',
      brightBlack: '#767676', brightRed: '#E74856', brightGreen: '#16C60C',
      brightYellow: '#F9F1A5', brightBlue: '#3B78FF', brightMagenta: '#B4009E',
      brightCyan: '#61D6D6', brightWhite: '#F2F2F2'
    }
  },
  {
    name: 'IBM 5153',
    theme: {
      background: '#000000', foreground: '#AAAAAA', cursor: '#00AA00',
      cursorAccent: '#000000', selectionBackground: '#FFFFFF',
      black: '#000000', red: '#AA0000', green: '#00AA00', yellow: '#C47E00',
      blue: '#0000AA', magenta: '#AA00AA', cyan: '#00AAAA', white: '#AAAAAA',
      brightBlack: '#555555', brightRed: '#FF5555', brightGreen: '#55FF55',
      brightYellow: '#FFFF55', brightBlue: '#5555FF', brightMagenta: '#FF55FF',
      brightCyan: '#55FFFF', brightWhite: '#FFFFFF'
    }
  }
];

export function normalizeTerminalThemeName(name: unknown): string {
  if (typeof name !== 'string') {
    return DEFAULT_THEME_NAME;
  }

  return TERMINAL_THEMES.find(theme => theme.name.toLowerCase() === name.toLowerCase())?.name
    ?? DEFAULT_THEME_NAME;
}

export function resolveTerminalTheme(name: string): ITheme {
  const normalized = normalizeTerminalThemeName(name);
  const preset = TERMINAL_THEMES.find(theme => theme.name === normalized)
    ?? KEVINZONDA_TERMINAL_DARK_THEME;
  return { ...preset.theme };
}

export function applyTerminalThemeToDocument(name: string): void {
  const theme = resolveTerminalTheme(name);
  const root = document.documentElement.style;
  const background = theme.background ?? '#0c0f14';
  const foreground = theme.foreground ?? '#d8dee9';
  const accent = theme.blue ?? '#5e81ac';
  document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')
    ?.setAttribute('content', background);
  root.setProperty('--terminal-background', background);
  for (const [property, value] of Object.entries(deriveChromePalette(background, foreground, accent))) {
    root.setProperty(property, value);
  }
}

// The window chrome (tab strips, sidebar, dividers) derives its palette from
// the terminal theme so a theme switch recolors the whole window instead of
// leaving the chrome stuck on the dark defaults. Mixing background toward
// foreground lightens dark themes and darkens light ones with one formula.
function deriveChromePalette(
  background: string,
  foreground: string,
  accent: string
): Record<string, string> {
  const towardFg = (t: number): string => mix(background, foreground, t);
  const bright = relativeLuminance(background) < 0.5
    ? mix(foreground, '#ffffff', 0.25)
    : mix(foreground, '#000000', 0.25);

  return {
    '--chrome-bg': towardFg(0.06),
    '--chrome-bg-translucent': toRgba(towardFg(0.06), 0.93),
    '--chrome-raised': towardFg(0.11),
    '--chrome-active': towardFg(0.16),
    '--chrome-hover': towardFg(0.21),
    '--chrome-border': towardFg(0.17),
    '--chrome-divider': towardFg(0.04),
    '--chrome-text': foreground,
    '--chrome-text-dim': mix(foreground, background, 0.3),
    '--chrome-text-bright': bright,
    '--chrome-accent': accent
  };
}

function mix(a: string, b: string, t: number): string {
  const [ar, ag, ab] = parseHex(a);
  const [br, bg, bb] = parseHex(b);
  return toHex(
    Math.round(ar + (br - ar) * t),
    Math.round(ag + (bg - ag) * t),
    Math.round(ab + (bb - ab) * t)
  );
}

function parseHex(color: string): [number, number, number] {
  const match = /^#([0-9a-f]{6})$/i.exec(color.trim());
  if (!match?.[1]) {
    return [0, 0, 0];
  }
  const value = parseInt(match[1], 16);
  return [(value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff];
}

function toHex(r: number, g: number, b: number): string {
  return `#${[r, g, b].map(channel => channel.toString(16).padStart(2, '0')).join('')}`;
}

function toRgba(hex: string, alpha: number): string {
  const [r, g, b] = parseHex(hex);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

function relativeLuminance(color: string): number {
  const [r, g, b] = parseHex(color);
  return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
}
