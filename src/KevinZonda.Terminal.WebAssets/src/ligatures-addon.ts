import { LigaturesAddon as XtermLigaturesAddon } from '@xterm/addon-ligatures';
import type { Terminal } from '@xterm/xterm';

// Match the fallback set in our pinned xterm addon (MIT, xterm.js authors).
// Longest matches must win, e.g. !== rather than != followed by =.
const FALLBACK_LIGATURES = [
  '<--', '<---', '<<-', '<-', '->', '->>', '-->', '--->',
  '<==', '<===', '<<=', '<=', '=>', '=>>', '==>', '===>', '>=', '>>=',
  '<->', '<-->', '<--->', '<---->', '<=>', '<==>', '<===>', '<====>', '::', ':::',
  '<~~', '</', '</>', '/>', '~~>', '==', '!=', '/=', '~=', '<>', '===', '!==', '!===',
  '<:', ':=', '*=', '*+', '<*', '<*>', '*>', '<|', '<|>', '|>', '+*', '=*', '=:', ':>',
  '/*', '*/', '+++', '<!--', '<!---'
].sort((a, b) => b.length - a.length);

export class LigaturesAddon extends XtermLigaturesAddon {
  private fallbackTerminal?: Terminal;
  private fallbackJoinerId?: number;

  public constructor() {
    // Our independent joiner supplies fallback ranges even after font loading.
    super({ fallbackLigatures: [] });
  }

  public override activate(terminal: Terminal): void {
    super.activate(terminal);
    this.fallbackTerminal = terminal;
    // Local Font Access can report a different family name from the CSS alias
    // (CaskaydiaCove Nerd Font vs CaskaydiaCove NF). The upstream addon then
    // parses a later fallback font, e.g. Cascadia Mono, and replaces its working
    // fallback ranges with an empty set. xterm merges ranges from both joiners,
    // keeping common ligatures alongside any font-specific ranges it discovers.
    this.fallbackJoinerId = terminal.registerCharacterJoiner(text => {
      const ranges: [number, number][] = [];
      for (let i = 0; i < text.length; i++) {
        const ligature = FALLBACK_LIGATURES.find(value => text.startsWith(value, i));
        if (ligature) {
          ranges.push([i, i + ligature.length]);
          i += ligature.length - 1;
        }
      }
      return ranges;
    });
  }

  public override dispose(): void {
    if (this.fallbackJoinerId !== undefined) {
      this.fallbackTerminal?.deregisterCharacterJoiner(this.fallbackJoinerId);
      this.fallbackJoinerId = undefined;
    }
    this.fallbackTerminal = undefined;
    super.dispose();
  }
}
