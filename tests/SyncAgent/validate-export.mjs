import { validateEnvelope } from '../../supabase/functions/_shared/sync.ts';
let input = '';
for await (const chunk of process.stdin) input += chunk;
const envelope = JSON.parse(input);
const invalid = envelope.Changes.filter(change => !validateEnvelope({ ...envelope, Changes: [change] }));
console.log(JSON.stringify({ valid: validateEnvelope(envelope), entities: envelope.Changes.length, rejectedTypes: [...new Set(invalid.map(change => change.EntityType))] }));
process.exitCode = validateEnvelope(envelope) ? 0 : 1;
