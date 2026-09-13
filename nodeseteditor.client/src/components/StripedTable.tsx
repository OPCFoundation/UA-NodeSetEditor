import * as React from 'react';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import { useTheme } from '@mui/material/styles';
import type { SxProps, Theme } from '@mui/material/styles';

export interface StripedTableColumn {
   key: string;
   label: string;
   width?: string | number;
}

interface StripedTableProps {
   columns: StripedTableColumn[];
   rows: Record<string, React.ReactNode>[];
   rowSx?: (SxProps<Theme> | undefined)[];
}

export const StripedTable: React.FC<StripedTableProps> = ({ columns, rows, rowSx }) => {
   const theme = useTheme();

   return (
      <TableContainer>
         <Table size="small">
            <TableHead>
               <TableRow>
                  {columns.map((col) => (
                     <TableCell key={col.key} sx={{ fontWeight: 'bold', width: col.width }}>
                        {col.label}
                     </TableCell>
                  ))}
               </TableRow>
            </TableHead>
            <TableBody>
               {rows.map((row, index) => (
                  <TableRow
                     key={index}
                     sx={{
                        // grey[50] is #fafafa in both light and dark modes
                        // (DarkTheme only swaps [200]/[600]) — using
                        // action.hover keeps the stripe theme-aware
                        // (translucent black in light, translucent white
                        // in dark).
                        backgroundColor: index % 2 === 0
                           ? theme.palette.background.paper
                           : theme.palette.action.hover,
                        ...((rowSx?.[index] ?? {}) as Record<string, unknown>),
                     }}
                  >
                     {columns.map((col) => (
                        <TableCell key={col.key}>
                           {row[col.key]}
                        </TableCell>
                     ))}
                  </TableRow>
               ))}
            </TableBody>
         </Table>
      </TableContainer>
   );
};
