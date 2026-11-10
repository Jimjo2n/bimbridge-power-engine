# Security

This is a development candidate; no supported production release or response-time guarantee is declared.

Report vulnerabilities privately using GitHub private vulnerability reporting if enabled. If unavailable, contact @Jimjo2n through an established private channel. Do not place exploit details, model files, credentials or personal data in public issues. No security email address is invented here.

Include affected commit, environment, impact and a minimal sanitized reproduction. Dynamic C# executes powerful Revit API operations; review the [execution policy](docs/0.9.0/DYNAMIC_CODE_EXECUTION_POLICY.md). Use isolated test models and the existing permission controls.

Never commit tokens, signing keys, local descriptors, operational logs, real BIM models or customer PDFs. Rotate exposed secrets; deleting the latest file does not remove a secret from history.

CI uses read-only permissions by default. Draft release creation receives write access only in its specific workflow. PR jobs must not consume privileged secrets or use a live Revit installation. Repository visibility can only change following explicit owner confirmation.
