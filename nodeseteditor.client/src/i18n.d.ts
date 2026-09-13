/* eslint-disable @typescript-eslint/no-empty-object-type */
// import the original type declarations
import "i18next";

declare module "i18next" {
   // Extend CustomTypeOptions
   interface CustomTypeOptions {
      // custom resources type
      resources: {};
      // other
   }
}