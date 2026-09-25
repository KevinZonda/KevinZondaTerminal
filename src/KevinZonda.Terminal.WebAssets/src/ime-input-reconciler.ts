import type { IDisposable, Terminal } from '@xterm/xterm';

// xterm normally sends printable keys from keydown. An IME can instead commit
// them with insertText, sometimes before its keydown and sometimes while the
// previous key is still down. Use that committed text if xterm did not send it.
export function installImeInputReconciler(
  terminal: Terminal,
  forward: (data: string) => void
): IDisposable {
  const textarea = terminal.textarea;
  const element = terminal.element;
  if (!textarea || !element) {
    throw new Error('The terminal must be opened before installing IME input reconciliation.');
  }

  let composing = false;
  let activeKeyCode: string | undefined;
  let suppressedImeCode: string | undefined;
  let inputInProgress = false;
  const keyOutput = new Set<string>();
  const inputOutput = new Set<string>();
  const dataListener = terminal.onData(data => {
    if (activeKeyCode !== undefined) keyOutput.add(data);
    if (inputInProgress) inputOutput.add(data);
  });

  // xterm's keyCode=229 path diffs the textarea on a timer. When the input
  // precedes keydown, that snapshot already includes the new text and sends
  // nothing; when it follows keydown, the timer can duplicate insertText.
  // Let the committed input event own non-composing IME text instead.
  terminal.attachCustomKeyEventHandler(event => {
    if (event.type === 'keyup') {
      if (event.code === suppressedImeCode) suppressedImeCode = undefined;
      return true;
    }
    if (event.type === 'keypress') {
      return event.code !== suppressedImeCode;
    }
    if (composing || event.isComposing || event.ctrlKey || event.altKey || event.metaKey) {
      suppressedImeCode = undefined;
      return true;
    }
    if (event.keyCode === 229 || event.key === 'Process') {
      suppressedImeCode = event.code;
      return false;
    }
    suppressedImeCode = undefined;
    return true;
  });

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.target !== textarea) return;
    activeKeyCode = event.code;
    keyOutput.clear();
  };
  const onKeyUp = (event: KeyboardEvent): void => {
    if (event.target !== textarea || event.code !== activeKeyCode) return;
    activeKeyCode = undefined;
    keyOutput.clear();
  };
  const onInputCapture = (event: Event): void => {
    if (event.target !== textarea) return;
    inputInProgress = true;
    inputOutput.clear();
  };
  const onInput = (event: InputEvent): void => {
    inputInProgress = false;
    if (!composing && !event.isComposing && event.inputType === 'insertText' &&
        event.data &&
        !keyOutput.has(event.data) && !inputOutput.has(event.data)) {
      forward(event.data);
    }
    keyOutput.clear();
    inputOutput.clear();
  };
  const onCompositionStart = (): void => {
    composing = true;
    suppressedImeCode = undefined;
  };
  const onCompositionEnd = (): void => { composing = false; };

  element.addEventListener('keydown', onKeyDown, { capture: true });
  element.addEventListener('keyup', onKeyUp, { capture: true });
  element.addEventListener('input', onInputCapture, { capture: true });
  textarea.addEventListener('input', onInput);
  textarea.addEventListener('compositionstart', onCompositionStart);
  textarea.addEventListener('compositionend', onCompositionEnd);

  return {
    dispose: () => {
      dataListener.dispose();
      terminal.attachCustomKeyEventHandler(() => true);
      element.removeEventListener('keydown', onKeyDown, { capture: true });
      element.removeEventListener('keyup', onKeyUp, { capture: true });
      element.removeEventListener('input', onInputCapture, { capture: true });
      textarea.removeEventListener('input', onInput);
      textarea.removeEventListener('compositionstart', onCompositionStart);
      textarea.removeEventListener('compositionend', onCompositionEnd);
    }
  };
}
