import { useRoute } from "../lib/router";
import InvestigationsPage from "./InvestigationsPage";
import RecommendationsPage from "./RecommendationsPage";
import ExperimentsPage from "./ExperimentsPage";

/** Insights: the AI's checks (#/insights, #/insights/inv/:id), suggestions and to-dos, and experiments. */
export default function InsightsPage() {
  const route = useRoute();
  if (route.section === "suggestions") return <RecommendationsPage />;
  if (route.section === "experiments") return <ExperimentsPage />;
  return <InvestigationsPage />;
}
