// Launch load test (testing.md §6): 300 concurrent shoppers browsing, 30 paying; API p95 < 400 ms, no errors.
// Run against STAGING (sandbox providers, seeded catalogue, rate limits relaxed for the load-generator IP), never production:
//   k6 run -e API=https://api.staging.rahiq.example tests/load/k6-launch.js
// Payers use cash on delivery so every order completes without a webhook; card flows are covered by CommerceSuite.
import http from "k6/http";
import { check, group, sleep } from "k6";
import { SharedArray } from "k6/data";

const API = __ENV.API ?? "http://localhost:5080";
const json = { headers: { "Content-Type": "application/json", "Accept-Language": "tr" } };

export const options = {
  scenarios: {
    browse: { executor: "ramping-vus", exec: "browse", startVUs: 0, stages: [{ duration: "1m", target: 300 }, { duration: "5m", target: 300 }, { duration: "30s", target: 0 }] },
    pay: { executor: "constant-vus", exec: "pay", vus: 30, duration: "6m", startTime: "30s" },
  },
  thresholds: {
    http_req_failed: ["rate<0.001"],
    "http_req_duration{kind:api}": ["p(95)<400"],
    "checks{flow:pay}": ["rate>0.99"],
  },
};

const slugs = new SharedArray("slugs", () => ["kestane-bali", "sidr-bali", "kekik-bali", "cam-bali", "oud-taif-rose", "sabah-bergamotu", "amber-meclisi"]);
const pick = (list) => list[Math.floor(Math.random() * list.length)];
const tag = { tags: { kind: "api" } };

export function browse() {
  group("browse", () => {
    http.get(`${API}/api/catalog/products?section=${pick(["honey", "perfume"])}&locale=tr`, tag);
    sleep(1 + Math.random() * 2);
    const res = http.get(`${API}/api/catalog/products/${pick(slugs)}?locale=tr`, tag);
    check(res, { "product 200": (r) => r.status === 200 });
    sleep(2 + Math.random() * 3);
    http.get(`${API}/api/catalog/search?q=${pick(["bal", "oud", "kestane", "gül"])}&locale=tr`, tag);
    sleep(1 + Math.random() * 2);
  });
}

export function pay() {
  group("pay", () => {
    const product = http.get(`${API}/api/catalog/products/${pick(slugs.slice(0, 4))}?locale=tr`, tag).json();
    const add = http.post(`${API}/api/cart/lines`, JSON.stringify({ variantId: product.variants[0].id, qty: 1 }), { ...json, ...tag });
    const token = add.headers["X-Cart-Token"];
    const cart = { headers: { ...json.headers, "X-Cart-Token": token }, ...tag };
    const checkoutId = http.post(`${API}/api/checkout`, "{}", cart).json();
    const email = `load-${__VU}-${__ITER}@example.com`;
    http.put(`${API}/api/checkout/${checkoutId}`, JSON.stringify({
      email, phone: "+905551112233", isGift: false, hidePrices: false, marketingConsent: false,
      shippingAddress: { fullName: "Yük Testi", phone: "+905551112233", provinceCode: 34, district: "Kadıköy", line1: "Test Sk. 1" },
    }), cart);
    const view = http.get(`${API}/api/checkout/${checkoutId}?paymentMethod=cod`, cart).json();
    sleep(3);
    const res = http.post(`${API}/api/checkout/${checkoutId}/pay`, JSON.stringify({ paymentMethod: "cod", acceptedContractsHash: view.contracts.hash }),
      { headers: { ...cart.headers, "Idempotency-Key": `${__VU}-${__ITER}-${Date.now()}` }, tags: { kind: "api", flow: "pay" } });
    check(res, { "order placed": (r) => r.status === 200 }, { flow: "pay" });
    sleep(5);
  });
}
