import React from "react";
import ReactDOM from "react-dom/client";
// Tokens first: every stylesheet after this reads its colours, sizes and spacing from them.
import "./tokens.css";
// Inter Variable, self-hosted (only the scripts a page uses are downloaded, via unicode-range).
import "@fontsource-variable/inter";
import "./styles.css";
import "./charts.css";
import App from "./App";
import { ErrorBoundary } from "./components/ui/ErrorBoundary";
import { observeTableLabels } from "./lib/responsiveTables";
observeTableLabels();
ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ErrorBoundary label="Joule" root>
      <App />
    </ErrorBoundary>
  </React.StrictMode>,
);
