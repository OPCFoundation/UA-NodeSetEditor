import React from 'react';
import { Tooltip, Typography, type TypographyProps } from '@mui/material';
import type { SxProps, Theme } from '@mui/material/styles';

interface TruncatedTextProps {
   text: string;
   sx?: SxProps<Theme>;
   variant?: TypographyProps['variant'];
   color?: TypographyProps['color'];
}

const TruncatedText: React.FC<TruncatedTextProps> = ({ text, sx, variant, color }) => {
   const textRef = React.useRef<HTMLElement>(null);
   const [isTruncated, setIsTruncated] = React.useState(false);

   React.useEffect(() => {
      const el = textRef.current;
      if (el) {
         setIsTruncated(el.scrollWidth > el.clientWidth);
      }
   }, [text]);

   const mergedSx: SxProps<Theme> = {
      display: 'block',
      overflow: 'hidden',
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
      maxWidth: '100%',
      ...((sx ?? {}) as Record<string, unknown>),
   };

   const typography = (
      <Typography ref={textRef} variant={variant} color={color} sx={mergedSx}>
         {text}
      </Typography>
   );

   return isTruncated ? (
      <Tooltip title={text} enterDelay={300}>
         {typography}
      </Tooltip>
   ) : (
      typography
   );
};

export default TruncatedText;
