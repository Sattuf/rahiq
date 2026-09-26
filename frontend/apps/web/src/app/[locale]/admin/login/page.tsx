import type { Metadata } from "next";
import { StaffLogin } from "@/components/admin/StaffLogin";

export const metadata: Metadata = { robots: { index: false, follow: false }, title: "Admin" };

export default function AdminLoginPage() {
  return (
    <main id="main" className="container narrow section">
      <StaffLogin />
    </main>
  );
}
