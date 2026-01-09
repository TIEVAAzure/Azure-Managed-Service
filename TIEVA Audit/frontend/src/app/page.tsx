"use client";

import { useEffect, useState } from "react";
import { api, ServiceTier, Customer } from "@/lib/api";
import Link from "next/link";

export default function Dashboard() {
  const [tiers, setTiers] = useState<ServiceTier[]>([]);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    Promise.all([api.getTiers(), api.getCustomers()])
      .then(([t, c]) => {
        setTiers(t);
        setCustomers(c);
      })
      .catch(console.error)
      .finally(() => setLoading(false));
  }, []);

  if (loading) {
    return (
      <div className="p-6">
        <div className="text-white text-lg">Loading...</div>
      </div>
    );
  }

  const totalModules = tiers.length > 0 ? tiers[0].modules.length : 10;

  return (
    <div className="p-6">
      <h1 className="text-2xl font-bold text-white mb-2">Dashboard</h1>
      <p className="text-white/80 mb-6">TIEVA Assessment Management Portal</p>

      {/* Stats Row */}
      <div className="grid grid-cols-4 gap-4 mb-6">
        <StatCard label="Customers" value={customers.length} icon="🏢" />
        <StatCard label="Service Tiers" value={tiers.length} icon="🎯" />
        <StatCard label="Assessment Modules" value={totalModules} icon="📋" />
        <StatCard label="Assessments (30d)" value={0} icon="📊" />
      </div>

      {/* Two Column Layout */}
      <div className="grid grid-cols-2 gap-6">
        {/* Recent Customers */}
        <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <div className="p-4 border-b border-gray-200 flex justify-between items-center">
            <h2 className="font-semibold text-[#1B365D]">Recent Customers</h2>
            <Link href="/customers" className="text-sm text-[#2DA58E] hover:underline">
              View all →
            </Link>
          </div>
          <div className="p-4">
            {customers.length === 0 ? (
              <p className="text-gray-500 text-sm py-4 text-center">
                No customers yet.{" "}
                <Link href="/customers" className="text-[#2DA58E] hover:underline">
                  Add one
                </Link>
              </p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-gray-500 border-b">
                    <th className="pb-2 font-medium">Name</th>
                    <th className="pb-2 font-medium">Industry</th>
                    <th className="pb-2 font-medium">Subs</th>
                  </tr>
                </thead>
                <tbody>
                  {customers.slice(0, 5).map((c) => (
                    <tr key={c.id} className="border-b last:border-0">
                      <td className="py-2 font-medium">{c.name}</td>
                      <td className="py-2 text-gray-600">{c.industry || "-"}</td>
                      <td className="py-2">{c.subscriptionCount}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </div>

        {/* Service Tiers */}
        <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <div className="p-4 border-b border-gray-200 flex justify-between items-center">
            <h2 className="font-semibold text-[#1B365D]">Service Tiers</h2>
            <Link href="/tiers" className="text-sm text-[#2DA58E] hover:underline">
              Configure →
            </Link>
          </div>
          <div className="p-4 grid grid-cols-2 gap-3">
            {tiers.map((tier) => (
              <div
                key={tier.id}
                className="border-2 rounded-lg p-3 text-center"
                style={{ borderColor: tier.color }}
              >
                <div className="font-semibold" style={{ color: tier.color }}>
                  {tier.displayName}
                </div>
                <div className="text-2xl font-bold mt-1">{tier.moduleCount}</div>
                <div className="text-xs text-gray-500">modules</div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function StatCard({ label, value, icon }: { label: string; value: number; icon: string }) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-4">
      <div className="flex items-center justify-between">
        <div>
          <p className="text-xs text-gray-500 uppercase tracking-wide">{label}</p>
          <p className="text-3xl font-bold mt-1 text-[#1B365D]">{value}</p>
        </div>
        <div className="text-2xl">{icon}</div>
      </div>
    </div>
  );
}