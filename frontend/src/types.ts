export type PaymentStatus =
  | 'PendingFraud'
  | 'FraudApproved'
  | 'FraudReview'
  | 'Declined'
  | 'Settled'
  | 'SettlementFailed';

export const ALL_STATUSES: PaymentStatus[] = [
  'PendingFraud', 'FraudApproved', 'FraudReview', 'Settled', 'Declined', 'SettlementFailed',
];

export const STATUS_LABEL: Record<PaymentStatus, string> = {
  PendingFraud: 'Scoring',
  FraudApproved: 'Settling',
  FraudReview: 'In review',
  Declined: 'Declined',
  Settled: 'Settled',
  SettlementFailed: 'Settlement failed',
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
  createdAt: string;
  updatedAt: string;
}

export interface NewPayment {
  amount: number;
  currency: string;
  customerId: string;
  merchantId: string;
  country: string;
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
