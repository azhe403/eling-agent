import type { NextConfig } from "next";

const isDev = process.env.NODE_ENV === "development";
const backendPort = process.env.ELING_BACKEND_PORT || "4417";

const nextConfig: NextConfig = {
  // Next.js allows the hostname the dev server was started with, plus
  // "localhost". The dev script binds 0.0.0.0, so a tab opened on
  // http://127.0.0.1:4427 is a DIFFERENT hostname and the dev server answers
  // its script requests with 403: React never hydrates, so the page renders
  // its shell and the data never arrives. Allow the loopback alias too.
  // Hostnames only - no scheme, no port - because that is what is matched.
  allowedDevOrigins: ["127.0.0.1"],
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
  experimental: isDev
    ? {
        // Reclaim in-memory Turbopack cache after persisting to disk
        turbopackMemoryEviction: "full",
        // GC unreachable work in memory + persistent cache (16.4.0+)
        turbopackGc: true,
      }
    : {},
};

export default nextConfig;
