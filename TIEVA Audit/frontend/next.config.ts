import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  async rewrites() {
    return [
      {
        source: "/api/:path*",
        destination: "https://ca-tieva-api.mangobush-b32228a2.uksouth.azurecontainerapps.io/api/:path*",
      },
    ];
  },
};

export default nextConfig;