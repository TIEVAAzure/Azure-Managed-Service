"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

const navItems = [
  { href: "/", label: "Dashboard", icon: "📊" },
  { href: "/customers", label: "Customers", icon: "🏢" },
  { href: "/assessments", label: "Assessments", icon: "📋" },
  { href: "/tiers", label: "Service Tiers", icon: "🎯" },
  { href: "/connections", label: "Connections", icon: "🔗" },
];

export default function Sidebar() {
  const pathname = usePathname();

  return (
    <div className="w-64 bg-black text-white flex flex-col">
      <div className="p-5 border-b border-white/10">
        <h1 className="text-lg font-semibold tracking-wide">TIEVA Portal</h1>
        <p className="text-xs text-white/50 mt-1">Assessment Management</p>
      </div>

      <nav className="flex-1 p-3">
        {navItems.map((item) => {
          const isActive = pathname === item.href || 
            (item.href !== "/" && pathname.startsWith(item.href));
          
          return (
            <Link
              key={item.href}
              href={item.href}
              className={`flex items-center gap-3 px-4 py-3 rounded-lg mb-1 transition-colors text-sm ${
                isActive
                  ? "bg-[#2DA58E] text-white"
                  : "text-white/70 hover:bg-white/10 hover:text-white"
              }`}
            >
              <span>{item.icon}</span>
              <span>{item.label}</span>
            </Link>
          );
        })}
      </nav>

      <div className="p-4 border-t border-white/10 text-xs text-white/40">
        Connected to Azure
        <span className="inline-block w-2 h-2 bg-green-400 rounded-full ml-2"></span>
      </div>
    </div>
  );
}