import type { IDisposable, Terminal } from '@xterm/xterm';

// Some IMEs report a printable punctuation key as Process with its physical
// keyCode rather than 229. xterm ignores that keydown, then ignores the
// composed input event because a keydown was seen. Forward that committed text.
export function installImeInputFallback(
  terminal: Terminal,
  forward: (data: string) => void
): IDisposable {
  const textarea = terminal.textarea;
  if (!textarea) {
    throw new Error('The terminal must be opened before installing its IME fallback.');
  }

  let pendingProcessKey = false;
  let composing = false;
  let sentSinceKeydown: string[] = [];
  const dataListener = terminal.onData(data => sentSinceKeydown.push(data));

  const onKeyDown = (event: KeyboardEvent): void => {
    sentSinceKeydown = [];
    pendingProcessKey = event.key === 'Process' && event.keyCode !== 229 &&
      !event.ctrlKey && !event.altKey && !event.metaKey;
  };
  const onKeyUp = (): void => {
    pendingProcessKey = false;
  };
  const onCompositionStart = (): void => {
    composing = true;
    pendingProcessKey = false;
  };
  const onCompositionEnd = (): void => {
    composing = false;
  };
  const onInput = (event: InputEvent): void => {
    if (!pendingProcessKey || composing || event.isComposing ||
        event.inputType !== 'insertText' || !event.composed || !event.data) {
      return;
    }

    pendingProcessKey = false;
    if (!sentSinceKeydown.includes(event.data)) {
      forward(event.data);
    }
  };

  textarea.addEventListener('keydown', onKeyDown, { capture: true });
  textarea.addEventListener('keyup', onKeyUp, { capture: true });
  textarea.addEventListener('compositionstart', onCompositionStart);
  textarea.addEventListener('compositionend', onCompositionEnd);
  textarea.addEventListener('input', onInput);

  return {
    dispose: () => {
      dataListener.dispose();
      textarea.removeEventListener('keydown', onKeyDown, { capture: true });
      textarea.removeEventListener('keyup', onKeyUp, { capture: true });
      textarea.removeEventListener('compositionstart', onCompositionStart);
      textarea.removeEventListener('compositionend', onCompositionEnd);
      textarea.removeEventListener('input', onInput);
    }
  };
}
