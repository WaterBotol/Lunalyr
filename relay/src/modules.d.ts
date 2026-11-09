// Wrangler bundles .html files as text modules by default.
declare module "*.html" {
  const content: string;
  export default content;
}
