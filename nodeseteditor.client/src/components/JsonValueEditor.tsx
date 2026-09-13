import * as React from 'react';
import { useTheme } from '@mui/material/styles';
import addFormats from 'ajv-formats';
import {
   ColorPicker,
   createJSONEditor,
   createAjvValidator,
   expandMinimal,
   isSvelteComponentRenderer,
   Mode,
   renderValue,
   type Content,
   type JsonEditor,
   type JSONSchema,
   type MenuItem,
   type OnClassName,
   type RenderValueProps,
   type Validator,
} from 'vanilla-jsoneditor';

interface JsonValueEditorProps {
   /** Draft 2020-12 JSON Schema describing the current element. Optional. */
   schema?: Record<string, unknown>;
   /** Current value (any JSON-able type, or null/undefined). */
   value: unknown;
   /** Called whenever the user edits the value. May be debounced upstream. */
   onChange: (value: unknown) => void;
   readOnly?: boolean;
   /** Initial editor mode. Defaults to 'tree'. */
   mode?: 'tree' | 'text';
   /** Editor pane height. */
   height?: number | string;
   /**
    * Called when the user clicks a NodeId-typed value (schema format
    * "UaNodeId" or "UaExpandedNodeId"). The argument is the raw NodeId
    * string. Implementations typically open a view-node dialog.
    */
   onNodeIdClick?: (nodeId: string) => void;
   /**
    * When true, keeps vanilla-jsoneditor's "table" mode-switcher button
    * available. Defaults to false (stripped) since the editor is normally
    * fed a single element at a time.
    */
   allowTableMode?: boolean;
}

/**
 * Thin React wrapper around vanilla-jsoneditor. Mounts on first render,
 * destroys on unmount, syncs prop updates via updateProps. The wrapper
 * intentionally does not control the editor's internal mode or selection
 * state — only content, schema, and read-only flag.
 */
export const JsonValueEditor: React.FC<JsonValueEditorProps> = ({
   schema,
   value,
   onChange,
   readOnly,
   mode = 'tree',
   height = 360,
   onNodeIdClick,
   allowTableMode = false,
}) => {
   const theme = useTheme();
   const containerRef = React.useRef<HTMLDivElement | null>(null);
   const editorRef = React.useRef<JsonEditor | null>(null);
   const onChangeRef = React.useRef(onChange);
   React.useEffect(() => {
      onChangeRef.current = onChange;
   }, [onChange]);
   // Latest callback ref so the action attached at editor-mount time always
   // calls the current handler even if the prop updates later.
   const onNodeIdClickRef = React.useRef(onNodeIdClick);
   React.useEffect(() => {
      onNodeIdClickRef.current = onNodeIdClick;
   }, [onNodeIdClick]);
   // Schema is referenced from inside onRenderValue (which is captured at
   // mount); read through a ref so live schema updates don't go stale.
   const schemaRef = React.useRef(schema);
   React.useEffect(() => {
      schemaRef.current = schema;
   }, [schema]);
   // onRenderMenu is captured by createJSONEditor at mount time; route the
   // current allowTableMode value through a ref so toggling the prop later
   // takes effect on the next menu render.
   const allowTableModeRef = React.useRef(allowTableMode);
   React.useEffect(() => {
      allowTableModeRef.current = allowTableMode;
   }, [allowTableMode]);
   // Side-channel from onClassName → click delegate: keyed by the JSON path
   // (joined with "/"), value is the NodeId string. The click handler walks
   // the DOM up to a `.ua-nodeid-clickable` element, derives the path from
   // the editor's internal data-path attribute, and looks up the NodeId here.
   // Falls back to the element's text content when no entry exists.
   const nodeIdAtPathRef = React.useRef<Map<string, string>>(new Map());

   // Track the latest external value to avoid re-injecting it on every keystroke.
   const lastValueRef = React.useRef<unknown>(value);

   // Build the schema validator. By default Ajv only checks `type` (string vs
   // number etc.) and ignores `format` keywords — so a DateTime field declared
   // as {type:"string", format:"date-time"} would accept "not-a-date". Wire
   // ajv-formats into the Ajv instance via onCreateAjv so format-violating
   // edits surface as inline errors in tree mode.
   const validator: Validator | undefined = React.useMemo(() => {
      if (!schema) return undefined;
      try {
         return createAjvValidator({
            schema: schema as unknown as JSONSchema,
            onCreateAjv: (ajv) => {
               // ajv-formats supports the standard JSON Schema formats:
               // date-time, date, time, duration, email, uri, uuid, regex, etc.
               // Mode "full" (the default) does strict validation; "fast" skips
               // some edge cases for speed. The schema generator emits date-time,
               // uuid, and a few custom formats — full mode covers all standard
               // ones we use today.
               addFormats(ajv);
            },
         });
      } catch {
         return undefined;
      }
   }, [schema]);

   // Mount once.
   React.useEffect(() => {
      if (!containerRef.current) return;
      const initialContent: Content = { json: value === undefined ? null : value };
      const editor = createJSONEditor({
         target: containerRef.current,
         props: {
            content: initialContent,
            mode: mode === 'text' ? Mode.text : Mode.tree,
            mainMenuBar: true,
            navigationBar: true,
            statusBar: true,
            readOnly: !!readOnly,
            validator,
            // Strip "table" from the mode-switcher unless the caller opts in
            // via allowTableMode. The ValueEditor's default (paged) view feeds
            // a single element at a time, so table mode (an array-of-objects
            // spreadsheet) would be misleading. In single-view array mode the
            // entire array is fed in, and table is exactly the right view.
            onRenderMenu: (items: MenuItem[]) =>
               allowTableModeRef.current
                  ? items
                  : items.filter(item =>
                     !(item.type === 'button' && (item as { text?: string }).text === 'table')),
            // Suppress the built-in ColorPicker. The default renderer treats any
            // string matching a CSS color (named or hex) as a color and adds a
            // swatch — noisy and misleading for OPC UA strings that happen to
            // collide with color names. Keep all other built-in renderers.
            // RenderValueComponentDescription is a union of SvelteComponentRenderer
            // (has `component`) and SvelteActionRenderer (no `component`); only
            // the former can be the ColorPicker, so we narrow before comparing.
            onRenderValue: (props: RenderValueProps) =>
               renderValue(props).filter(d =>
                  !(isSvelteComponentRenderer(d) && d.component === ColorPicker)),
            // Mark NodeId-typed values with a className so the editor's
            // container-level dblclick listener can find them. We also stash
            // (path → value) in nodeIdAtPathRef so the listener can look up
            // the exact NodeId string for the path under the click target.
            // The Part 6 ExtensionObject wrapper (`UaTypeId`) is added by the
            // serialization layer, not the schema generator, so the schema
            // doesn't tag it with format=UaNodeId — we recognise it by name.
            onClassName: ((path, value) => {
               const isWrapperTypeId = path.length > 0
                  && path[path.length - 1] === 'UaTypeId';
               const fmt = isWrapperTypeId
                  ? 'UaNodeId'
                  : lookupFormatAtPath(schemaRef.current, path);
               if ((fmt === 'UaNodeId' || fmt === 'UaExpandedNodeId')
                   && typeof value === 'string'
                   && value.length > 0) {
                  nodeIdAtPathRef.current.set(path.join('/'), value);
                  return 'ua-nodeid-clickable';
               }
               return undefined;
            }) as OnClassName,
            onChange: (content: Content) => {
               // Content is either { json } or { text }. Normalize to a JSON value.
               const next = 'json' in content
                  ? content.json
                  : safeParse(content.text);
               lastValueRef.current = next;
               onChangeRef.current(next);
            },
         },
      });
      editorRef.current = editor;
      lastValueRef.current = initialContent.json;

      // Expand the root so the user immediately sees the structure of the
      // current element (otherwise tree mode opens collapsed).
      editor.expand([], expandMinimal);

      // Delegated double-click listener for NodeId-typed values. onClassName
      // tags those rows with `.ua-nodeid-clickable`; this listener walks up
      // from the click target to find that class, then recovers the NodeId
      // either from our path-keyed map (populated in onClassName) or, as a
      // last-resort fallback, from the element's text content.
      // Pull the NodeId from a dblclicked NodeId-typed value. We try, in
      // order: a data-path attribute (most accurate when vanilla-jsoneditor
      // surfaces one) → look up our path-keyed map; otherwise a NodeId-
      // shaped substring of the element's textContent (works even when the
      // marked element is a whole row including the field label).
      const container = containerRef.current;
      const onDblClick = (e: MouseEvent) => {
         const target = e.target as HTMLElement | null;
         const hit = target?.closest<HTMLElement>('.ua-nodeid-clickable');
         if (!hit) return;
         const pathAttr = hit.getAttribute('data-path')
            ?? hit.closest<HTMLElement>('[data-path]')?.getAttribute('data-path');
         let nodeId: string | undefined;
         if (pathAttr) {
            try {
               const parsed = JSON.parse(pathAttr) as unknown;
               if (Array.isArray(parsed)) {
                  nodeId = nodeIdAtPathRef.current.get(parsed.join('/'));
               }
            } catch {
               nodeId = nodeIdAtPathRef.current.get(pathAttr);
            }
         }
         if (!nodeId) {
            const text = hit.textContent ?? '';
            const match = text.match(/(?:nsu=[^;\s"]+;|ns=\d+;)?[isgb]=[^\s"]+/);
            nodeId = match?.[0];
         }
         if (!nodeId) return;
         e.preventDefault();
         e.stopPropagation();
         onNodeIdClickRef.current?.(nodeId);
      };
      container.addEventListener('dblclick', onDblClick, true);

      return () => {
         container.removeEventListener('dblclick', onDblClick, true);
         editor.destroy();
         editorRef.current = null;
      };
      // eslint-disable-next-line react-hooks/exhaustive-deps
   }, []); // mount/unmount only

   // Push value updates from props when they actually differ from what the user is editing.
   React.useEffect(() => {
      const editor = editorRef.current;
      if (!editor) return;
      if (deepEqualJson(lastValueRef.current, value)) return;
      const next = value === undefined ? null : value;
      editor.update({ json: next });
      lastValueRef.current = next;
      // Re-expand the root when an external value swap happens (e.g. user
      // navigates to a different array element).
      editor.expand([], expandMinimal);
   }, [value]);

   // Push schema/readOnly changes via updateProps.
   React.useEffect(() => {
      editorRef.current?.updateProps({ validator, readOnly: !!readOnly });
   }, [validator, readOnly]);

   // vanilla-jsoneditor's menu/status bars are coloured via the CSS custom property
   // `--jse-theme-color`. Setting it on the container makes the bars match the app
   // header/footer (MUI primary), with the hover variant for menu-button states.
   const editorThemeStyle = {
      height,
      width: '100%',
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      ['--jse-theme-color' as any]: theme.palette.primary.main,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      ['--jse-theme-color-highlight' as any]: theme.palette.primary.dark,
   } as React.CSSProperties;

   return <div ref={containerRef} style={editorThemeStyle} />;
};

/**
 * Walks the JSON Schema along the given JSON-pointer path and returns the
 * leaf schema's `format` keyword, or undefined. Resolves `$ref` against the
 * root's `$defs` and looks inside `properties` / `items` / `oneOf` branches —
 * just enough coverage for the schemas the DataType generator emits.
 */
function lookupFormatAtPath(
   rootSchema: Record<string, unknown> | undefined,
   path: ReadonlyArray<string | number>,
): string | undefined {
   if (!rootSchema) return undefined;
   let current: Record<string, unknown> | undefined = rootSchema;
   const root = rootSchema;
   const resolve = (s: Record<string, unknown> | undefined): Record<string, unknown> | undefined => {
      if (!s) return s;
      const ref = typeof s.$ref === 'string' ? s.$ref : undefined;
      if (ref?.startsWith('#/$defs/')) {
         const key = ref.substring('#/$defs/'.length);
         const defs = root.$defs as Record<string, Record<string, unknown>> | undefined;
         return defs?.[key] ?? s;
      }
      return s;
   };
   for (const segment of path) {
      current = resolve(current);
      if (!current) return undefined;
      // Step into `items` for array indices.
      if (current.type === 'array' && current.items) {
         current = current.items as Record<string, unknown>;
         current = resolve(current);
         continue;
      }
      const props = current.properties as Record<string, Record<string, unknown>> | undefined;
      if (props && typeof segment === 'string' && props[segment]) {
         current = props[segment];
         continue;
      }
      // Try oneOf branches (Unions, etc.).
      const oneOf = current.oneOf as Array<Record<string, unknown>> | undefined;
      if (oneOf && typeof segment === 'string') {
         const branch = oneOf.find(b => {
            const bp = b.properties as Record<string, unknown> | undefined;
            return bp && segment in bp;
         });
         if (branch) {
            const bp = branch.properties as Record<string, Record<string, unknown>>;
            current = bp[segment];
            continue;
         }
      }
      return undefined;
   }
   current = resolve(current);
   return current && typeof current.format === 'string' ? current.format : undefined;
}

/**
 * Svelte action: attaches a click listener to the rendered value element so
 * a NodeId can open the view-node dialog. Compatible with vanilla-jsoneditor's
 * SvelteActionRenderer contract.
 */
function safeParse(text: string): unknown {
   try {
      return JSON.parse(text);
   } catch {
      return text;
   }
}

function deepEqualJson(a: unknown, b: unknown): boolean {
   if (a === b) return true;
   if (a === null || b === null) return a === b;
   if (typeof a !== typeof b) return false;
   if (typeof a !== 'object') return a === b;
   if (Array.isArray(a) !== Array.isArray(b)) return false;
   if (Array.isArray(a) && Array.isArray(b)) {
      if (a.length !== b.length) return false;
      for (let i = 0; i < a.length; i++) {
         if (!deepEqualJson(a[i], b[i])) return false;
      }
      return true;
   }
   const ao = a as Record<string, unknown>;
   const bo = b as Record<string, unknown>;
   const ak = Object.keys(ao);
   const bk = Object.keys(bo);
   if (ak.length !== bk.length) return false;
   for (const k of ak) {
      if (!Object.prototype.hasOwnProperty.call(bo, k)) return false;
      if (!deepEqualJson(ao[k], bo[k])) return false;
   }
   return true;
}
