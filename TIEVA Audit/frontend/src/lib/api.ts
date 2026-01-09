const API_BASE = "";

async function fetchApi<T>(endpoint: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${endpoint}`, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...options?.headers,
    },
  });

  if (!res.ok) {
    throw new Error(`API error: ${res.status}`);
  }

  return res.json();
}

// Types
export interface ServiceTier {
  id: string;
  name: string;
  displayName: string;
  description: string;
  color: string;
  moduleCount: number;
  subscriptionCount: number;
  modules: TierModule[];
}

export interface TierModule {
  moduleId: string;
  code: string;
  name: string;
  icon: string;
  isIncluded: boolean;
  frequency: string;
}

export interface Customer {
  id: string;
  name: string;
  code: string;
  industry: string;
  primaryContact: string;
  email: string;
  isActive: boolean;
  connectionCount: number;
  subscriptionCount: number;
  lastAssessment: string | null;
  lastScore: number | null;
}

export interface Assessment {
  id: string;
  customerId: string;
  customerName: string;
  status: string;
  startedAt: string | null;
  completedAt: string | null;
  subscriptionCount: number | null;
  findingCount: number | null;
  highFindings: number;
  mediumFindings: number;
  lowFindings: number;
  scoreOverall: number | null;
}

export interface DashboardStats {
  customerCount: number;
  subscriptionCount: number;
  assessmentCount30d: number;
  avgScore: number;
  recentAssessments: Assessment[];
}

export interface AssessmentModule {
  id: string;
  code: string;
  name: string;
  description: string;
  icon: string;
  category: string;
  estimatedMinutes: number;
  isActive: boolean;
  sortOrder: number;
}

// API Functions
export const api = {
  getDashboard: () => fetchApi<DashboardStats>("/api/dashboard"),
  getTiers: () => fetchApi<ServiceTier[]>("/api/tiers"),
  getModules: () => fetchApi<AssessmentModule[]>("/api/modules"),
  getCustomers: () => fetchApi<Customer[]>("/api/customers"),
  getCustomer: (id: string) => fetchApi<Customer>(`/api/customers/${id}`),
  createCustomer: (data: Partial<Customer>) =>
    fetchApi<Customer>("/api/customers", {
      method: "POST",
      body: JSON.stringify(data),
    }),
  getAssessments: (customerId?: string) =>
    fetchApi<Assessment[]>(`/api/assessments${customerId ? `?customerId=${customerId}` : ""}`),
};