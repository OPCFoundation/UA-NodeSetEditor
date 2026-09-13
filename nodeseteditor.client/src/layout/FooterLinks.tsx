import Button from '@mui/material/Button';
import Link from '@mui/material/Link';
import Typography from '@mui/material/Typography';

export interface FooterLinkItem {
   /** Text shown for the link. */
   label: string;
   /** Target URL. Rendered verbatim, so only use trusted constants. */
   href: string;
}

/**
 * The full OPC Foundation copyright/contact/policy link set shown in the app
 * footer. Callers can pass a different array to `FooterLinks` to render a
 * subset (or a different set) suited to their context.
 */
export const defaultFooterLinks: FooterLinkItem[] = [
   {
      label: `© OPC Federation AISBL ${new Date().getFullYear()}`,
      href: 'https://opcfoundation.org/about/what-is-opc/',
   },
   { label: 'Contact Us', href: 'https://opcfoundation.org/about/contact-us/' },
   { label: 'Become a Member', href: 'https://opcfoundation.org/membership/become-a-member/step1' },
   { label: 'Privacy Policy', href: 'https://opcfoundation.org/privacy-policy/' },
   { label: 'Terms of Use', href: 'https://opcfoundation.org/license/services/1.0/' },
];

interface FooterLinksProps {
   /** Links to render. Defaults to the full OPC Foundation set. */
   links?: FooterLinkItem[];
}

/**
 * Renders a row of external links (copyright, contact, policies). Every link
 * opens in a new tab. The set is configurable via `links` so the same control
 * can be reused across different contexts.
 */
export const FooterLinks = ({ links = defaultFooterLinks }: FooterLinksProps) => {
   return (
      <>
         {links.map((link) => (
            <Button key={link.href} sx={{ my: 2 }}>
               <Link href={link.href} target='_blank' rel='noopener noreferrer'>
                  <Typography variant='body2'>{link.label}</Typography>
               </Link>
            </Button>
         ))}
      </>
   );
};
