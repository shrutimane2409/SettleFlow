export type PaymentStatus =
  | 'PendingFraud'
  | 'FraudApproved'
  | 'FraudReview'
  | 'Declined'
  | 'Authorized'
  | 'Voided'
  | 'Settled'
  | 'SettlementFailed'
  | 'PartiallyRefunded'
  | 'Refunded';

export const ALL_STATUSES: PaymentStatus[] = [
  'PendingFraud', 'FraudReview', 'Authorized', 'FraudApproved', 'Settled', 'PartiallyRefunded', 'Refunded',
  'Declined', 'SettlementFailed', 'Voided',
];

export const STATUS_LABEL: Record<PaymentStatus, string> = {
  PendingFraud: 'Scoring',
  FraudApproved: 'Settling',
  FraudReview: 'In review',
  Declined: 'Declined',
  Settled: 'Settled',
  SettlementFailed: 'Settlement failed',
  Authorized: 'Authorized',
  Voided: 'Voided',
  PartiallyRefunded: 'Partially refunded',
  Refunded: 'Refunded',
};

export interface Payment {
  id: string;
  amount: number;
  currency: string;
  customerId: string;
  merchantId: string;
  country: string;
  status: PaymentStatus;
  riskScore: number | null;
  riskReasons: string[];
  settlementReference: string | null;
  failureReason: string | null;
  reviewedBy: string | null;
  reviewNote: string | null;
  captureMode: 'automatic' | 'manual';
  capturedAmount: number | null;
  refundedAmount: number;
  refundableAmount: number;
  authorizedAt: string | null;
  settledAt: string | null;
  voidedAt: string | null;
  feeAmount: number | null;
  payoutId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface Refund {
  id: string;
  paymentId: string;
  merchantId: string;
  amount: number;
  currency: string;
  reason: string | null;
  status: 'Pending' | 'Succeeded' | 'Failed';
  acquirerReference: string | null;
  failureReason: string | null;
  createdBy: string;
  createdAt: string;
  completedAt: string | null;
}

export interface WebhookDelivery {
  id: string;
  merchantId: string;
  eventType: string;
  paymentId: string | null;
  url: string;
  status: 'Pending' | 'Delivered' | 'Failed';
  attempts: number;
  nextAttemptAt: string;
  lastStatusCode: number | null;
  lastError: string | null;
  createdAt: string;
  deliveredAt: string | null;
}

export interface NewPayment {
  amount: number;
  currency: string;
  customerId: string;
  merchantId: string;
  country: string;
  captureMode?: 'automatic' | 'manual';
}

export interface Page<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface TimelineEntry {
  type: string;
  source: string;
  detail: string | null;
  occurredAt: string;
}

export interface Notification {
  id: number;
  paymentId: string;
  type: 'SETTLED' | 'FAILED' | 'REVIEW_REQUIRED' | string;
  message: string;
  createdAt: string;
}

export interface DeadLetter {
  id: string;
  consumer: string;
  topic: string;
  partition: number;
  offset: number;
  key: string | null;
  payload: string;
  error: string;
  attempts: number;
  createdAt: string;
  replayedAt: string | null;
}

export interface Stats {
  total: number;
  byStatus: Record<PaymentStatus, number>;
  settledVolume: { currency: string; volume: number; count: number }[];
  averageRiskScore: number | null;
  successRate: number | null;
  reviewQueue: number;
  throughput: { minute: string; count: number }[];
  pipeline: { outboxPending: number; outboxLagSeconds: number; deadLetters: number };
}

export interface Merchant {
  id: string;
  name: string;
  apiKeyPrefix: string;
  disabled: boolean;
  createdAt: string;
  keyRotatedAt: string | null;
  webhookUrl: string | null;
  feeBps: number;
  feeFixed: number;
}

export interface LedgerAccount {
  id: string;
  kind: 'acquirer_clearing' | 'merchant_balance' | 'platform_fees';
  owner: string;
  currency: string;
  balance: number;
  debitMinusCredit: number;
  entries: number;
}

export interface LedgerEntryRow {
  id: number;
  amount: number;
  type: string;
  reference: string;
  description: string;
  occurredAt: string;
}

export interface TrialBalance {
  balanced: boolean;
  currencies: { currency: string; debits: number; credits: number; net: number; balanced: boolean }[];
}

export interface Payout {
  id: string;
  merchantId: string;
  currency: string;
  grossAmount: number;
  feeAmount: number;
  refundAmount: number;
  netAmount: number;
  paymentCount: number;
  refundCount: number;
  status: 'Paid' | 'Failed';
  reference: string;
  createdAt: string;
  paidAt: string | null;
}

export interface PayoutDetail {
  payout: Payout;
  payments: { id: string; customerId: string; captured: number; fee: number; settlementReference: string; settledAt: string }[];
  refunds: { id: string; paymentId: string; amount: number; acquirerReference: string; completedAt: string }[];
}

export interface ReconRun {
  id: string;
  periodStart: string;
  periodEnd: string;
  startedAt: string;
  triggeredBy: string;
  matched: number;
  discrepancies: number;
  unresolved: number;
  ledgerBalanced: boolean;
}

export interface ReconItem {
  id: string;
  kind: 'AmountMismatch' | 'MissingAtAcquirer' | 'MissingInSettleFlow' | 'MissingLedgerPosting' | 'LedgerImbalance';
  operation: string;
  reference: string | null;
  merchantId: string | null;
  currency: string;
  settleFlowAmount: number | null;
  acquirerAmount: number | null;
  details: string;
  resolvedAt: string | null;
  resolvedBy: string | null;
  resolutionNote: string | null;
}

export interface User {
  id: string;
  username: string;
  role: 'admin' | 'analyst' | 'viewer';
  disabled: boolean;
  locked: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

export interface AuditEntry {
  id: number;
  actor: string;
  actorType: 'user' | 'merchant' | 'system';
  action: string;
  targetType: string | null;
  targetId: string | null;
  details: string | null;
  ipAddress: string | null;
  traceId: string | null;
  occurredAt: string;
}
