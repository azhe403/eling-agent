import type { NextConfig } from "next";

const isDev = process.env.NODE_ENV === "development";
const backendPort = process.env.ELING_BACKEND_PORT || "4417";

const nextConfig: NextConfig = {
  // Use export only for production builds served by ASP.NET binary
  ...(isDev
    ? {
        async rewrites() {
          return [
            {
              source: "/api/:path*",
              destination: `http://127.0.0.1:${backendPort}/api/:path*`,
            },
          ];
        },
      }
    : {
        output: "export",
        trailingSlash: true,
      }),
};

export default nextConfig;
