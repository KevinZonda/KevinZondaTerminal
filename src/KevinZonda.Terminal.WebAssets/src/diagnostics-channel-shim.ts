interface NoopChannel {
  readonly hasSubscribers: false;
  publish(data: unknown): void;
}

interface NoopTracingChannel {
  readonly hasSubscribers: false;
  tracePromise<T>(callback: () => Promise<T>): Promise<T>;
}

const noopChannel: NoopChannel = {
  hasSubscribers: false,
  publish: () => undefined
};

const noopTracingChannel: NoopTracingChannel = {
  hasSubscribers: false,
  tracePromise: callback => callback()
};

export function channel(_name: string): NoopChannel {
  return noopChannel;
}

export function tracingChannel(_name: string): NoopTracingChannel {
  return noopTracingChannel;
}
