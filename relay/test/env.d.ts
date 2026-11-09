// Bindings seen by tests through `env` from "cloudflare:workers".
declare namespace Cloudflare {
  interface Env {
    ROOMS: DurableObjectNamespace<import("../src/room").Room>;
  }
}
