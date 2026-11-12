import { render } from "solid-js/web";
import { App } from "./App";
import { applyTheme } from "./theme";
import { lang } from "./i18n";
import "./styles/app.css";

applyTheme();
document.documentElement.lang = lang();
render(() => <App />, document.getElementById("root")!);
