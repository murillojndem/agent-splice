/**
 * The shapes `openapi/agentsplice-openapi.yaml` publishes.
 *
 * Every field the gateway can leave out is optional here, and every field it can send as null is
 * nullable. That is not defensive typing: absence is the gateway's way of saying it does not know,
 * and a type that promised a number would make the dashboard invent one (FR-DASH-006, FR-TRACE-006).
 */

export type ExchangeStatus = 'accepted' | 'forwarding' | 'streaming' | 'completed' | 'cancelled' | 'failed';

export type ContentRetentionState =
  | 'disabled'
  | 'metadata_only'
  | 'sanitized_content'
  | 'expired'
  | 'deleted';

export type MeasurementProvenance =
  | 'measured'
  | 'client_reported'
  | 'upstream_reported'
  | 'runtime_log'
  | 'estimated'
  | 'inferred';

export type RuntimeHealthStatus =
  | 'unknown'
  | 'healthy'
  | 'unreachable'
  | 'authentication_failed'
  | 'incompatible_response'
  | 'no_models';

export interface ExchangeSummary {
  exchangeId: string;
  requestId: string;
  traceId?: string;
  startedAt: string;
  completedAt: string | null;
  status: ExchangeStatus;
  runtimeId?: string;
  /** Absent when the request was refused before its model was readable. */
  clientModelId?: string;
  upstreamModelId?: string;
  /** Null when the request never stated a preference, which is not the same as `false`. */
  streaming: boolean | null;
  contentRetentionState: ContentRetentionState;
}

export interface Measurement {
  name: string;
  value: number;
  unit: string;
  provenance: MeasurementProvenance;
  confidence?: number;
}

export interface ExchangeDetail extends ExchangeSummary {
  ingressProtocol: string;
  streamTermination: string;
  failureClass?: string;
  errorCode?: string;
  upstreamStatusCode?: number;
  measurements: Measurement[];
  /** Passed through as the gateway stored it; the schema is open on purpose. */
  structuralSummary?: Record<string, unknown>;
  responseSummary?: Record<string, unknown>;
  usage?: Record<string, unknown>;
}

export interface ExchangePage {
  items: ExchangeSummary[];
  nextCursor: string | null;
}

export interface TimelineObservation {
  sequence: number;
  type: string;
  timestamp: string;
  source: string;
  confidence?: number;
  safeDetails?: Record<string, string>;
}

export interface SystemInfo {
  version: string;
  stage: string;
  enabledModules: string[];
  contentRetentionEnabled: boolean;
  metadataRetentionEnabled: boolean;
}

export interface RuntimeSummary {
  runtimeId: string;
  provider: string;
  baseUrl: string;
  /** The name of an environment variable, never a key. */
  apiKeyEnvironmentVariable: string | null;
  enabled: boolean;
  discoveryEnabled: boolean;
}

export interface RuntimeHealth {
  runtimeId: string;
  status: RuntimeHealthStatus;
  /** Absent for a runtime nothing has consulted. */
  checkedAt?: string;
  servedFromStaleCache: boolean;
}

export interface CatalogModel {
  clientModelId: string;
  runtimeId: string;
  upstreamModelId: string;
  aliasId?: string;
  source: string;
  /** Null when discovery has never run for the owning runtime. */
  reachable: boolean | null;
  capabilityProvenance: string;
  /** Absent when nothing reported one. Never zero — that would be 1970. */
  created?: number;
}

export interface ErrorEnvelope {
  error: {
    message: string;
    type: string;
    code?: string;
    param?: string | null;
  };
}
